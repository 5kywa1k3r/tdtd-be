using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.NativeStatisticTests;

// Dedicated Mongo only. Activation and the already-published configuration are
// fixture inputs. Membership, payload storage/reader, all six legacy stages,
// Direct caller, transaction runner, lease CAS and replay are product services.
internal static class PublicationCases
{
    internal static async Task Run(Func<string, Func<Task>, Task> test, string connection, string database, Func<Task> restart)
    {
        var index = 0;
        async Task<Fixture> Fresh() => await Fixture.Create(connection, database + "_pub" + ++index);
        await test("legacy publication keeps hash and omits native receipt", () =>
        {
            Check(NativeStatisticPublicationContract.BindHash(Fixtures.Schema, null) == Fixtures.Schema, "legacy hash changed");
            Check(!new WorkReportStatisticRebuildJob().ToBsonDocument().Contains("nativeStatisticPublication"), "legacy BSON gained receipt");
            return Task.CompletedTask;
        });
        await test("publication actual caller, payload reader, transaction, receipt and replay", async () =>
        {
            var f = await Fresh(); var result = await f.Project();
            Check(result.State == "PUBLISHED" && !result.IsReplay, "not published");
            var job = await f.Job(); var artifact = await NativeStatisticPublicationContract.ReadAsync(f.Ctx.Db, job, default);
            Check(artifact.Sources.Count == 2 && artifact.Result.Groups.Count > 0, "missing native results");
            Check(job.DirectStoreDigests.Count == 6 && job.NativeStatisticPublication is not null, "incomplete publication");
            Check((await f.Project()).IsReplay, "exact replay missing");
            Check(await f.Ctx.WorkReportStatisticRebuildJobs.CountDocumentsAsync(FilterDefinition<WorkReportStatisticRebuildJob>.Empty) == 1, "duplicate job");
        });
        await test("native Direct reader returns typed full result with same receipt", async () =>
        {
            var f = await Fresh(); await f.Project(); var job = await f.Job();
            var response = await f.Reader().ReadNativeAsync(f.Request(), default);
            Check(response.Metadata.State == "READY" && response.Result?.Sources.Count == 2
                && response.Result.Groups.Count > 0 && response.ArtifactHash == job.NativeStatisticPublication?.ArtifactHash, "readback mismatch");
        });
        await test("native reader denies partial assignment access and unknown work before existence", async () =>
        {
            var f = await Fresh(); await f.Project(); var other = "cccccccccccccccccccccccc";
            await f.Ctx.WorkAssignments.UpdateOneAsync(x => x.Id == f.Reports[0].WorkAssignmentId,
                Builders<WorkAssignment>.Update.Set(x => x.LeaderWatcherUserIds, new List<string> { other }));
            async Task Forbidden(NativeStatisticResultRequest request)
            {
                try { await f.Reader(other).ReadNativeAsync(request, default); }
                catch (tdtd_be.Common.Errors.AppException ex) when (ex.Code == tdtd_be.Common.Errors.AppErrorCode.STAT_RUN_FORBIDDEN) { return; }
                throw new Exception("scope was not forbidden");
            }
            await Forbidden(f.Request()); await Forbidden(f.Request() with { WorkId = "dddddddddddddddddddddddd" });
        });
        await test("native reader reports config and source-order drift as stale without result", async () =>
        {
            var f = await Fresh(); await f.Project();
            await f.Ctx.WorkAssignmentReports.UpdateOneAsync(x => x.Id == f.Reports[1].Id,
                Builders<WorkAssignmentReport>.Update.Set(x => x.PayloadUpdatedAtUtc, f.Reports[1].PayloadUpdatedAtUtc!.Value.AddMilliseconds(1)));
            var stale = await f.Reader().ReadNativeAsync(f.Request(), default);
            Check(stale.Metadata.State == "STALE" && stale.Result is null, "source drift exposed result");
            await f.Ctx.DynamicFormTemplates.UpdateOneAsync(x => x.Id == f.Template.Id,
                Builders<DynamicFormTemplate>.Update.Inc(x => x.StatisticConfigRevision, 1));
            stale = await f.Reader().ReadNativeAsync(f.Request(), default);
            Check(stale.Metadata.State == "STALE" && stale.Result is null, "config drift exposed result");
        });
        await test("native publication survives Mongo restart and new projector/reader", async () =>
        {
            var f = await Fresh(); await f.Project(); var before = await f.Reader().ReadNativeAsync(f.Request(), default);
            await restart();
            Check((await f.Project()).IsReplay, "restart did not replay");
            var after = await f.Reader().ReadNativeAsync(f.Request(), default);
            Check(StatConfigCanonicalJson.Canonicalize(before) == StatConfigCanonicalJson.Canonicalize(after), "restart changed result");
        });
        await test("native concurrent callers publish one generation", async () =>
        {
            var f = await Fresh();
            async Task<StatRunDirectProjectionResult?> Attempt() { try { return await f.Project(); } catch (InvalidOperationException) { return null; } }
            var outcomes = await Task.WhenAll(Attempt(), Attempt());
            Check(outcomes.Any(r => r?.State == "PUBLISHED"), "no caller published");
            Check(await f.Ctx.WorkReportStatisticRebuildJobs.CountDocumentsAsync(x => x.Status == "COMPLETED") == 1, "duplicate publication");
            Check((await f.Ctx.Works.Find(x => x.Id == f.Work).SingleAsync()).DirectPublicationRevision == 1, "duplicate publication counter");
        });
        await test("publication Foundation refresh uses same native stage and receipt", async () =>
        {
            var f = await Fresh(); var report = f.Reports[0]; var owner = f.Template; var binding = f.Activation.Binding;
            var json = JsonSerializer.Serialize(new {
                CapabilityId = StatRunCapabilities.DirectFieldTableLabel, RunKind = WorkReportStatisticRebuildJobRunKinds.Foundation,
                report.WorkId, ScopeType = "WORK", ScopeId = report.WorkId, SourceReportId = report.Id,
                SourceRevision = report.PayloadRevision, SourceHash = report.PayloadHash, report.LifecycleRevision,
                DynamicFormTemplateId = owner.Id, ConfigId = owner.StatisticConfigId, ConfigVersionId = owner.StatisticConfigVersionId,
                ConfigVersionNo = owner.StatisticConfigVersionNo, ConfigRevision = owner.StatisticConfigRevision, ConfigHash = owner.StatisticConfigHash,
                binding.CatalogVersion, binding.CatalogRawSha256, binding.CatalogSemanticSha256, binding.SchemaRawSha256,
                binding.SchemaSemanticSha256, binding.StageLockSha256, CandidateChainId = binding.ChainId,
                report.PeriodKey, report.PeriodInstanceKey, report.PeriodKind, PeriodStartUtc = report.PeriodStart, PeriodEndUtc = report.PeriodEnd
            });
            var pin = JsonSerializer.Deserialize<StatRunFoundationRefreshPin>(json)!;
            var result = await f.Service().ProjectFoundationRefreshAsync(pin, report.LifecycleProjectionOutbox.Last().EntryKey, f.Actor);
            Check(result.State == "PUBLISHED" && (await f.Job()).NativeStatisticPublication is not null, "foundation missing native");
        });
        await test("publication EXCLUDE membership omits report before native calculation", async () =>
        {
            var f = await Fresh(); await f.Ctx.WorkAssignmentReports.UpdateOneAsync(x => x.Id == f.Reports[1].Id,
                Builders<WorkAssignmentReport>.Update.Set(x => x.CumulativeContributionMode, "EXCLUDE"));
            await f.Project(); var artifact = await NativeStatisticPublicationContract.ReadAsync(f.Ctx.Db, await f.Job(), default);
            Check(artifact.Sources.Count == 1 && artifact.Sources[0].ReportId == f.Reports[0].Id, "excluded source counted");
        });
        foreach (var fault in new[] { "lease", "source-time", "config", "actor", "contribution", "work-revision", "artifact", "catalog", "assignment", "period" })
            await test("publication transaction rejects " + fault + " drift and rolls back fences", async () =>
            {
                var f = await Fresh(); var hookReached = false;
                var result = await RejectOrZero(() => f.Project(async () =>
                {
                    hookReached = true;
                    if (fault == "lease") await f.Ctx.WorkReportStatisticRebuildJobs.UpdateManyAsync(x => x.Status == "RUNNING",
                        Builders<WorkReportStatisticRebuildJob>.Update.Set(x => x.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1)));
                    if (fault == "source-time") await f.Ctx.WorkAssignmentReports.UpdateOneAsync(x => x.Id == f.Reports[1].Id,
                        Builders<WorkAssignmentReport>.Update.Set(x => x.PayloadUpdatedAtUtc, f.Reports[1].PayloadUpdatedAtUtc!.Value.AddMilliseconds(1)));
                    if (fault == "config") await f.Ctx.DynamicFormTemplates.UpdateOneAsync(x => x.Id == f.Template.Id,
                        Builders<DynamicFormTemplate>.Update.Inc(x => x.StatisticConfigRevision, 1));
                    if (fault == "actor") await f.Ctx.Users.UpdateOneAsync(x => x.Id == f.Actor,
                        Builders<AppUser>.Update.Set(x => x.UnitId, ObjectId.GenerateNewId().ToString()));
                    if (fault == "contribution") await f.Ctx.WorkAssignmentReports.UpdateOneAsync(x => x.Id == f.Reports[1].Id,
                        Builders<WorkAssignmentReport>.Update.Set(x => x.CumulativeContributionMode, "EXCLUDE"));
                    if (fault == "work-revision") await f.Ctx.Works.UpdateOneAsync(x => x.Id == f.Work,
                        Builders<Work>.Update.Inc(x => x.DirectSourceRevision, 1));
                    if (fault == "artifact") await f.Ctx.Db.GetCollection<BsonDocument>(NativeStatisticArtifactStore.CollectionName)
                        .UpdateOneAsync(new BsonDocument("kind", "chunk"), new BsonDocument("$set", new BsonDocument("sha256", Fixtures.Schema)));
                    if (fault == "catalog") f.Activation.Binding = f.Activation.Binding with { CatalogSemanticSha256 = new string('b', 64) };
                    if (fault == "assignment") await f.Ctx.WorkAssignments.UpdateOneAsync(x => x.Id == f.Reports[1].WorkAssignmentId,
                        Builders<WorkAssignment>.Update.Set(x => x.IsActive, false));
                    if (fault == "period") await f.Ctx.WorkReportPeriods.UpdateOneAsync(x => x.Id == f.Reports[1].WorkReportPeriodId,
                        Builders<WorkReportPeriod>.Update.Inc(x => x.SourceLifecycleRevision, 1));
                }));
                Check(hookReached && result, "fault did not reach publication boundary or drift published");
                Check(await f.Ctx.WorkReportStatisticRebuildJobs.CountDocumentsAsync(x => x.Status == "COMPLETED") == 0, "completed after drift");
                var owner = await f.Ctx.Db.GetCollection<BsonDocument>(f.Ctx.DynamicFormTemplates.CollectionNamespace.CollectionName)
                    .Find(new BsonDocument("_id", ObjectId.Parse(f.Template.Id))).SingleAsync();
                Check(!owner.Contains("nativeStatisticPublicationFence"), "transaction fence not rolled back");
                Check((await f.Ctx.Works.Find(x => x.Id == f.Work).SingleAsync()).DirectPublicationRevision == 0, "publication counter advanced");
            });
        await test("publication corrupt READY artifact cannot replay or repair", async () =>
        {
            var f = await Fresh(); await f.Project(); var job = await f.Job();
            var raw = f.Ctx.Db.GetCollection<BsonDocument>(NativeStatisticArtifactStore.CollectionName);
            await raw.UpdateOneAsync(new BsonDocument("kind", "chunk"), new BsonDocument("$set", new BsonDocument("sha256", Fixtures.Schema)));
            var before = (await raw.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync()).ToJson();
            Check(await RejectOrZero(() => f.Project()), "corrupt replay accepted");
            Check(before == (await raw.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync()).ToJson(), "corrupt history repaired");
        });
        await test("publication recall non-trigger report with no legacy rows rebuilds native", async () =>
        {
            var f = await Fresh(); await f.Project(); var prior = await f.Job();
            var report = f.Reports[1]; report.Status = WorkAssignmentReportStatus.Submitted; report.LifecycleRevision++;
            SetLifecycle(report, f.Actor, "REVIEW_RECALL_APPROVED", "APPROVED", "SUBMITTED");
            await f.Ctx.WorkAssignmentReports.ReplaceOneAsync(x => x.Id == report.Id, report);
            await f.Ctx.WorkReportPeriods.UpdateOneAsync(x => x.Id == report.WorkReportPeriodId,
                Builders<WorkReportPeriod>.Update.Set(x => x.Status, WorkReportPeriodStatus.Submitted)
                    .Set(x => x.SourceLifecycleRevision, report.LifecycleRevision));
            await f.Ctx.Works.UpdateOneAsync(x => x.Id == f.Work, Builders<Work>.Update.Inc(x => x.DirectSourceRevision, 1));
            var result = await f.Service().ProjectLifecycleEntryAsync(report.Id, report.LifecycleProjectionOutbox.Last().EntryKey, f.Actor);
            Check(result.State == "PUBLISHED", "recall not published");
            var job = await f.Ctx.WorkReportStatisticRebuildJobs.Find(x => x.Id == result.RunId).SingleAsync();
            var artifact = await NativeStatisticPublicationContract.ReadAsync(f.Ctx.Db, job, default);
            Check(artifact.Sources.Count == 1 && job.ReversalAudit?.PriorRunId == prior.Id, "recall membership/audit missing");
            Check(!(await f.Ctx.WorkReportStatisticRebuildJobs.Find(x => x.Id == prior.Id).SingleAsync()).IsCurrentPublication, "old publication still current");
            var historical = await f.Reader().ReadNativeAsync(f.Request() with { Historical = true, RunId = prior.Id, GenerationId = prior.GenerationId }, default);
            Check(historical.Metadata.Freshness == "HISTORICAL" && historical.Result?.Sources.Count == 2, "history replaced by current");
            var current = await f.Reader().ReadNativeAsync(f.Request(), default);
            Check(current.Metadata.Freshness == "FRESH" && current.Result?.Sources.Count == 1, "current did not read reversal");
            var last = f.Reports[0]; last.Status = WorkAssignmentReportStatus.Submitted; last.LifecycleRevision++;
            SetLifecycle(last, f.Actor, "REVIEW_RECALL_APPROVED", "APPROVED", "SUBMITTED");
            await f.Ctx.WorkAssignmentReports.ReplaceOneAsync(x => x.Id == last.Id, last);
            await f.Ctx.WorkReportPeriods.UpdateOneAsync(x => x.Id == last.WorkReportPeriodId,
                Builders<WorkReportPeriod>.Update.Set(x => x.Status, WorkReportPeriodStatus.Submitted)
                    .Set(x => x.SourceLifecycleRevision, last.LifecycleRevision));
            await f.Ctx.Works.UpdateOneAsync(x => x.Id == f.Work, Builders<Work>.Update.Inc(x => x.DirectSourceRevision, 1));
            Check((await f.Service().ProjectLifecycleEntryAsync(last.Id, last.LifecycleProjectionOutbox.Last().EntryKey, f.Actor)).State == "PUBLISHED",
                "last-source recall did not publish empty generation");
            var empty = await f.Reader().ReadNativeAsync(f.Request(), default);
            Check(empty.Metadata.State == "READY" && empty.Result?.Sources.Count == 0, "last-source recall retained old values");
        });
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static async Task<bool> RejectOrZero(Func<Task<StatRunDirectProjectionResult>> operation)
    {
        try { return (await operation()).State == "ZERO_WRITE"; }
        catch (Exception e) when (e is tdtd_be.Common.Errors.AppException or InvalidOperationException or WorkReportDirectGenerationValidationException) { return true; }
    }
    private static void SetLifecycle(WorkAssignmentReport report, string actor, string operation = "REVIEW_APPROVE",
        string from = "SUBMITTED", string to = "APPROVED")
    {
        var command = ObjectId.GenerateNewId().ToString();
        var entry = new WorkReportLifecycleProjectionOutboxEntry {
            CommandId = command, EntryKey = WorkReportLifecycleOutboxContract.ComputeEntryKey(command, report.LifecycleRevision, operation),
            LifecycleRevision = report.LifecycleRevision, Operation = operation, ActorUserId = actor,
            FromStatus = from, ToStatus = to, FromIsActive = true, ToIsActive = true,
            PayloadRevision = report.PayloadRevision, PayloadHash = report.PayloadHash!, State = WorkReportLifecycleProjectionOutboxStates.Pending,
            CreatedAtUtc = new DateTime(2026, 9, 17, 1, 0, report.LifecycleRevision, DateTimeKind.Utc)
        };
        report.LifecycleProjectionOutbox.Add(entry); report.LastLifecycleCommandId = command;
        report.LastLifecycleCommandHash = Fixtures.Schema; report.LastLifecycleCommandOperation = operation;
        report.LastLifecycleCommandRevision = report.LifecycleRevision; report.LastLifecycleCommandPayloadRevision = report.PayloadRevision;
        report.LastLifecycleCommandStatus = report.Status; report.LastLifecycleCommandIsActive = true;
    }

    internal sealed class Fixture
    {
        internal required MongoDbContext Ctx;
        internal required WorkReportPayloadService Payload;
        internal required DynamicFormTemplate Template;
        internal required WorkAssignmentReport[] Reports;
        internal required Activation Activation;
        internal string Actor = "aaaaaaaaaaaaaaaaaaaaaaaa", Work = "111111111111111111111111";
        private readonly MeAccessor me = new(new HttpContextAccessor());
        internal static async Task<Fixture> Create(string connection, string database, Func<MongoDbContext, Task<DynamicFormTemplate>>? configure = null)
        {
            var ctx = new MongoDbContext(Microsoft.Extensions.Options.Options.Create(new MongoOptions { ConnectionString = connection, Database = database }));
            var input = Fixtures.LockedUnitInput(owner: configure is null ? null : await configure(ctx));
            var f = new Fixture { Ctx = ctx, Payload = new(ctx), Template = input.Template,
                Reports = input.Sources.Select(s => s.Report).ToArray(), Activation = new(database) };
            var unit = "bbbbbbbbbbbbbbbbbbbbbbbb";
            await ctx.Users.InsertOneAsync(new AppUser { Id = f.Actor, Username = "canvas-test", FullName = "Canvas test", PasswordHash = "fixture", UnitId = unit });
            await ctx.Works.InsertOneAsync(new Work { Id = f.Work, DirectSourceRevision = 1 });
            if (configure is null) await ctx.DynamicFormTemplates.InsertOneAsync(f.Template);
            else await ctx.DynamicFormTemplates.ReplaceOneAsync(t => t.Id == f.Template.Id, f.Template, new ReplaceOptions { IsUpsert = true });
            for (var i = 0; i < f.Reports.Length; i++)
            {
                var report = f.Reports[i]; var assignment = (100 + i).ToString("x24"); var period = (200 + i).ToString("x24");
                report.WorkAssignmentId = assignment; report.WorkReportPeriodId = period; report.AssigneeUserId = f.Actor;
                report.ApprovedByUserId = f.Actor; report.PeriodKey = "2026-09"; report.PeriodKind = "MONTH";
                report.PayloadRevision = 0;
                var stored = await f.Payload.SaveReportPayloadAsync(report, "{}", null, input.Sources[i].Payload.TableValuesJson,
                    null, f.Actor, report.PayloadUpdatedAtUtc!.Value);
                report.PayloadRevision = stored.PayloadRevision; report.PayloadHash = stored.PayloadHash;
                report.PayloadSizeBytes = stored.PayloadSizeBytes; report.PayloadStatus = stored.PayloadStatus;
                SetLifecycle(report, f.Actor);
                await ctx.WorkAssignmentReports.InsertOneAsync(report);
                await ctx.WorkAssignments.InsertOneAsync(new WorkAssignment { Id = assignment, WorkId = f.Work, IsActive = true,
                    CreatedByUserId = f.Actor, IssuedByUnitId = unit, TargetUnitIds = [unit], Assignees = [new UserRef { UserId = f.Actor, UnitId = unit }],
                    DynamicFormTemplateId = f.Template.Id, DynamicFormFamilyId = f.Template.FamilyId,
                    DynamicFormVersionNo = f.Template.VersionNo, DynamicFormSchemaHash = f.Template.PublishedSchemaHash });
                await ctx.WorkReportPeriods.InsertOneAsync(new WorkReportPeriod { Id = period, WorkId = f.Work, WorkAssignmentId = assignment,
                    IsActive = true, AssigneeUserId = f.Actor, AssigneeUnitId = unit, Status = WorkReportPeriodStatus.Approved,
                    CurrentReportId = report.Id, SourceLifecycleReportId = report.Id, SourceLifecycleRevision = report.LifecycleRevision,
                    SourceLifecycleAppliedAtUtc = report.PayloadUpdatedAtUtc, PeriodInstanceKey = report.PeriodInstanceKey,
                    PeriodKey = report.PeriodKey, PeriodKind = report.PeriodKind });
            }
            return f;
        }
        internal StatRunDirectProjectionService Service(Func<Task>? beforeTransaction = null) => new(Ctx, Activation,
            new WorkReportFieldStatisticsService(Ctx, me, Payload), new WorkReportTableStatisticsService(Ctx, me, Payload),
            new WorkReportLabelStatisticsService(Ctx, me, Payload),
            new TransactionHook(new DynamicFlowDefinitionTransactionRunner(Ctx, NullLogger<DynamicFlowDefinitionTransactionRunner>.Instance), beforeTransaction),
            NullLogger<StatRunDirectProjectionService>.Instance, Payload);
        internal Task<StatRunDirectProjectionResult> Project(Func<Task>? hook = null) => Service(hook)
            .ProjectLifecycleEntryAsync(Reports[0].Id, Reports[0].LifecycleProjectionOutbox.Last().EntryKey, Actor);
        internal Task<WorkReportStatisticRebuildJob> Job() => Ctx.WorkReportStatisticRebuildJobs.Find(x => x.IsCurrentPublication).SingleAsync();
        internal NativeStatisticResultRequest Request() => new(Work, Reports[0].PeriodInstanceKey, Template.Id);
        internal P9DirectResultService Reader(string? actor = null)
        {
            var http = new DefaultHttpContext();
            http.Items[MeAccessor.MeItemKey] = new MeResponse(actor ?? Actor, "fixture", "fixture", [], "bbbbbbbbbbbbbbbbbbbbbbbb", null, null, null, [], null, false);
            return new(Ctx, new(new HttpContextAccessor { HttpContext = http }), Service());
        }
    }
    internal sealed class Activation(string database) : IStatRunCandidateActivation
    {
        internal StatRunCandidateBinding Binding = new("canvas-isolated", "canvas-native-publication", 10, "test-catalog",
            Fixtures.Schema, Fixtures.Schema, Fixtures.Schema, Fixtures.Schema, Fixtures.Schema, database, []);
        public StatRunCandidateBinding RequireFoundation(string capabilityId, string routeId) => Binding;
        public StatRunCandidateEvaluation EvaluateFoundation(string capabilityId, string routeId) => new(true, null, routeId, capabilityId, Binding);
        public StatRunCandidateBinding RequireCapability(string capabilityId, string routeId) => Binding;
        public StatRunCandidateEvaluation EvaluateCapability(string capabilityId, string routeId) => new(true, null, routeId, capabilityId, Binding);
    }
    private sealed class TransactionHook(IDynamicFlowDefinitionTransactionRunner inner, Func<Task>? hook) : IDynamicFlowDefinitionTransactionRunner
    {
        public async Task<T> ExecuteAsync<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> action, CancellationToken ct = default)
        { if (hook is not null) await hook(); return await inner.ExecuteAsync(action, ct); }
        public async Task ExecuteAsync(Func<IClientSessionHandle, CancellationToken, Task> action, CancellationToken ct = default)
        { if (hook is not null) await hook(); await inner.ExecuteAsync(action, ct); }
    }
}
