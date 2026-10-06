using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

// Controlled changes to fresh fixtures exercise HTTP/job/Apply reauthorization.
// They do not stand in for the product's Submit/Return/Approve lifecycle UAT.
internal static class TextAuthorityChecks
{
    private sealed record Replacement(IMongoCollection<BsonDocument> Collection,
        BsonDocument Before, BsonDocument During, BsonDocument After);

    internal static async Task Run(MongoDbContext db, AggregateMongoStore store, WorkReportPayloadService payloads, RuntimeFixture fixture,
        WorkAssignmentReport source2, AggregateRecipeDto recipe, AggregatePeriodContextDto context,
        string instanceId, AggregateMappingChangeDto change, string run,
        Func<string, object, string?, HttpStatusCode, Task<JsonElement>> post,
        Func<AggregateRecipeDto, AggregateInstanceSelectionDto?, Task<JsonElement>> rawJob,
        Action<bool, string> check, CancellationToken ct)
    {
        Task<JsonElement> Call(string path, object body, HttpStatusCode status = HttpStatusCode.OK)
            => post(path, body, fixture.Actor, status);
        var reports = db.Db.GetCollection<BsonDocument>(db.WorkAssignmentReports.CollectionNamespace.CollectionName);
        var assignments = db.Db.GetCollection<BsonDocument>(db.WorkAssignments.CollectionNamespace.CollectionName);
        var periods = db.Db.GetCollection<BsonDocument>(db.WorkReportPeriods.CollectionNamespace.CollectionName);
        var targetBefore = await db.WorkAssignmentReports.Find(r => r.Id == fixture.Report.Id).SingleAsync(ct);
        var artifacts = new List<object>();
        string? variantReportId = null;

        async Task<(string JobId, JsonElement Prepared, AggregateContentReadRequest Content,
            AggregateContentReadRequest Submission)> Fresh()
        {
            var job = await Call("preview-jobs/start", new AggregatePreviewJobRequest("MAPPING", context, null, instanceId, change));
            while (job.GetProperty("state").GetString() is "QUEUED" or "RUNNING")
            {
                await Task.Delay(150, ct);
                job = await Call("preview-jobs/" + job.GetProperty("id").GetString() + "/read", new { });
            }
            check(job.GetProperty("state").GetString() == "COMPLETED", "P05 fresh authorized text job completes");
            var prepared = job.GetProperty("result").Clone();
            var reference = prepared.GetProperty("preview").GetProperty("preview").GetProperty("results")
                .EnumerateArray().Single(r => r.GetProperty("portId").GetString() == "table").GetProperty("value");
            var content = new AggregateContentReadRequest(fixture.Report.Id, null, null, job.GetProperty("id").GetString(), reference);
            var submission = await Call("reports/" + fixture.Report.Id + "/submission-preview", new AggregateInstanceReadCommandDto(context));
            var submissionRef = submission.GetProperty("instances")[0].GetProperty("preview").GetProperty("results")
                .EnumerateArray().Single(r => r.GetProperty("portId").GetString() == "table").GetProperty("value");
            return (content.JobId!, prepared, content,
                new(fixture.Report.Id, null, null, null, submissionRef, ListReadId: submission.GetProperty("listReadId").GetString()));
        }

        async Task Rejected(string path, object request, HttpStatusCode status, string code, string label)
        {
            var response = await Call(path, request, status);
            check(response.GetProperty("code").GetString() == code, "P05 " + label + ": " + response.GetProperty("code"));
        }

        async Task Unwritten(string label)
        {
            var current = await db.WorkAssignmentReports.Find(r => r.Id == fixture.Report.Id).SingleAsync(ct);
            check(current.PayloadRevision == targetBefore.PayloadRevision && current.PayloadHash == targetBefore.PayloadHash
                && current.LifecycleRevision == targetBefore.LifecycleRevision, "P05 " + label + " preserves target payload/lifecycle");
        }

        // A returned/unapproved source invalidates both job-backed and submission-backed content grants.
        var statusPreview = await Fresh();
        var sourceBefore = await Document(reports, fixture.Source.Id, ct);
        var submitted = sourceBefore.DeepClone().AsBsonDocument;
        submitted["status"] = WorkAssignmentReportStatus.Submitted.ToString();
        submitted["lifecycleRevision"] = sourceBefore.GetValue("lifecycleRevision", 0).ToInt32() + 1;
        var restored = sourceBefore.DeepClone().AsBsonDocument;
        restored["lifecycleRevision"] = sourceBefore.GetValue("lifecycleRevision", 0).ToInt32() + 2;
        var statusReplacement = new Replacement(reports, sourceBefore, submitted, restored);
        await Backup(run, "source-status", [statusReplacement], ct);
        await Swap(db, [statusReplacement], false, ct);
        try
        {
            await Rejected("preview-jobs/" + statusPreview.JobId + "/read", new { }, HttpStatusCode.Conflict, "AGG_INPUT_STALE", "job read rejects changed source status");
            await Rejected("content/page", statusPreview.Content, HttpStatusCode.Conflict, "AGG_INPUT_STALE", "job page rejects changed source status");
            await Rejected("content/part", statusPreview.Content, HttpStatusCode.Conflict, "AGG_INPUT_STALE", "job part rejects changed source status");
            await Rejected("content/page", statusPreview.Submission, HttpStatusCode.Conflict, "AGG_INPUT_STALE", "submission page rejects changed source status");
            await Rejected("content/part", statusPreview.Submission, HttpStatusCode.Conflict, "AGG_INPUT_STALE", "submission part rejects changed source status");
            await Rejected("instances/" + instanceId + "/apply", new AggregateMappingApplyCommandDto(run + "-status-stale", context,
                change, statusPreview.Prepared.GetProperty("token").GetString()!), HttpStatusCode.Conflict, "AGG_CONFIRMATION_STALE", "Apply rejects source status changed after preview");
            var next = await rawJob(recipe, null);
            check(next.GetProperty("state").GetString() == "COMPLETED", "P05 fresh preview resolves current source status");
            var preview = next.GetProperty("result").GetProperty("preview");
            check(preview.GetProperty("contributingSources").GetArrayLength() == 1
                && preview.GetProperty("contributingSources")[0].GetProperty("reportId").GetString() == source2.Id
                && preview.GetProperty("linkedSources").GetArrayLength() == 2
                && preview.GetProperty("coverage").EnumerateArray().Any(c => c.GetProperty("state").GetString() == "SUBMITTED"),
                "P05 Submitted source remains tracked/linked but does not contribute a block");
            await Unwritten("source status rejection and fresh preview");
            artifacts.Add(new { scenario = "source-status", jobId = statusPreview.JobId, freshJobId = next.GetProperty("id").GetString() });
        }
        finally { await Restore(db, [statusReplacement]); }

        // Keep the report's binding and both published Forms owned by the actor.
        // Remove read/review authority from all synthetic source branches and their ancestor.
        var authorityPreview = await Fresh();
        var authorityChanges = new List<Replacement>();
        foreach (var id in new[] { context.AssignmentId, fixture.Source.WorkAssignmentId, source2.WorkAssignmentId })
        {
            var before = await Document(assignments, id, ct);
            if (before["workId"].AsObjectId.ToString() != fixture.WorkId) throw new InvalidOperationException("Fixture work scope mismatch");
            var during = before.DeepClone().AsBsonDocument;
            during["currentReviewerUserId"] = ObjectId.Parse(fixture.Outsider);
            during["leaderWatcherUserIds"] = new BsonArray();
            if (id == context.AssignmentId) during["assignees"] = new BsonArray();
            authorityChanges.Add(new(assignments, before, during, before));
        }
        await Backup(run, "source-authority", authorityChanges, ct);
        await Swap(db, authorityChanges, false, ct);
        try
        {
            var owned = await db.DynamicFormTemplates.Find(f => f.Id == fixture.SourceForm.Id).SingleAsync(ct);
            var targetBoot = await Call("editor/bootstrap", new { reportId = fixture.Report.Id });
            check(owned.CreatedByUserId == fixture.Actor && owned.IsPublished
                && targetBoot.GetProperty("context").GetProperty("reportId").GetString() == fixture.Report.Id,
                "P05 target remains accessible and source Form remains owned during source ACL revocation");
            var formPin = new AggregateFormPinDto(owned.Id, owned.FamilyId!, owned.VersionNo, owned.PublishedSchemaHash!);
            await Rejected("editor/source-schema", new AggregateEditorSchemaQueryDto(context, formPin), HttpStatusCode.NotFound,
                "AGG_SOURCE_UNAVAILABLE", "Form ownership does not grant report-source access");
            await Rejected("preview-jobs/" + authorityPreview.JobId + "/read", new { }, HttpStatusCode.NotFound,
                "AGG_SOURCE_UNAVAILABLE", "old job rejects revoked source authority");
            await Rejected("content/page", authorityPreview.Content, HttpStatusCode.NotFound, "AGG_SOURCE_UNAVAILABLE", "old job content rejects revoked source authority");
            await Rejected("content/page", authorityPreview.Submission, HttpStatusCode.NotFound, "AGG_SOURCE_UNAVAILABLE", "old submission content rejects revoked source authority");
            await Rejected("instances/" + instanceId + "/apply", new AggregateMappingApplyCommandDto(run + "-authority-stale", context,
                change, authorityPreview.Prepared.GetProperty("token").GetString()!), HttpStatusCode.NotFound, "AGG_SOURCE_UNAVAILABLE", "Apply rejects revoked source authority");
            await Unwritten("source ACL revocation");
            artifacts.Add(new { scenario = "source-authority", jobId = authorityPreview.JobId, assignmentIds = authorityChanges.Select(r => r.Before["_id"].ToString()).ToArray() });
        }
        finally { await Restore(db, authorityChanges); }

        // A historical Approved report cannot substitute for the current Submitted revision.
        sourceBefore = await Document(reports, fixture.Source.Id, ct);
        var periodBefore = await Document(periods, fixture.Source.WorkReportPeriodId!, ct);
        var newCurrent = BsonSerializer.Deserialize<WorkAssignmentReport>(sourceBefore);
        variantReportId = newCurrent.Id = ObjectId.GenerateNewId().ToString();
        newCurrent.VersionNo++;
        newCurrent.LifecycleRevision++;
        newCurrent.Status = WorkAssignmentReportStatus.Submitted;
        newCurrent.ApprovedAtUtc = null; newCurrent.ApprovedByUserId = null;
        newCurrent.AutoApprovedAtUtc = null; newCurrent.AutoApprovedByUserId = null;
        newCurrent.PayloadRevision = 0;
        var sourcePayload = await payloads.LoadReportPayloadAsync(BsonSerializer.Deserialize<WorkAssignmentReport>(sourceBefore), ct);
        var write = await payloads.SaveReportPayloadAsync(newCurrent, sourcePayload.Values1DJson, sourcePayload.FieldValuesJson,
            sourcePayload.TableValuesJson, null, newCurrent.AssigneeUserId, DateTime.UtcNow, ct);
        newCurrent.PayloadRevision = write.PayloadRevision; newCurrent.PayloadHash = write.PayloadHash;
        newCurrent.PayloadSizeBytes = write.PayloadSizeBytes; newCurrent.PayloadStatus = write.PayloadStatus;
        var historical = sourceBefore.DeepClone().AsBsonDocument; historical["isCurrent"] = false;
        historical["lifecycleRevision"] = sourceBefore.GetValue("lifecycleRevision", 0).ToInt32() + 1;
        restored = sourceBefore.DeepClone().AsBsonDocument;
        restored["lifecycleRevision"] = sourceBefore.GetValue("lifecycleRevision", 0).ToInt32() + 2;
        var periodDuring = periodBefore.DeepClone().AsBsonDocument; periodDuring["currentReportId"] = ObjectId.Parse(newCurrent.Id);
        var versionChanges = new[] { new Replacement(reports, sourceBefore, historical, restored), new Replacement(periods, periodBefore, periodDuring, periodBefore) };
        var serializedCurrent = newCurrent.ToBsonDocument();
        // Mongo stores _id first; whole-document equality is order-sensitive.
        var currentDocument = new BsonDocument("_id", serializedCurrent["_id"])
            .AddRange(serializedCurrent.Elements.Where(e => e.Name != "_id"));
        await Backup(run, "current-version", versionChanges, ct);
        await Swap(db, versionChanges, false, ct, currentDocument);
        try
        {
            // The version fixture declares its own data window; never infer it from DueAt/PeriodStart.
            var declarationRow = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Declarations)
                .Find(new BsonDocument("_id", "REPORT:" + fixture.Source.Id)).SingleAsync(ct);
            var declaration = AggregateMongoTransaction.Read<AggregateDeclarationState>(declarationRow).Value with { Identity = newCurrent.Id };
            await store.ExecuteAsync(async (transaction, token) =>
            {
                await transaction.PutAsync(AggregateCollections.Declarations, "REPORT:" + newCurrent.Id, 0, declaration,
                    fixture.WorkId, newCurrent.Id, [], token);
                return true;
            }, ct);
            var next = await rawJob(recipe, null);
            check(next.GetProperty("state").GetString() == "COMPLETED", "P05 current Submitted version preview completes with known coverage: " + next.GetProperty("errorCode"));
            var preview = next.GetProperty("result").GetProperty("preview");
            check(preview.GetProperty("linkedSources").EnumerateArray().Any(p => p.GetProperty("reportId").GetString() == newCurrent.Id)
                && !preview.GetProperty("linkedSources").EnumerateArray().Any(p => p.GetProperty("reportId").GetString() == fixture.Source.Id)
                && preview.GetProperty("contributingSources").GetArrayLength() == 1
                && preview.GetProperty("contributingSources")[0].GetProperty("reportId").GetString() == source2.Id,
                "P05 current unapproved version is tracked; historical Approved never falls back into contributors");
            var reference = preview.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("portId").GetString() == "table").GetProperty("value");
            var page = await Call("content/page", new AggregateContentReadRequest(fixture.Report.Id, null, null,
                next.GetProperty("id").GetString(), reference));
            check(page.GetProperty("rows").GetArrayLength() == 1, "P05 current-only content table includes exactly the still-approved source");
            check((await payloads.LoadReportPayloadAsync(BsonSerializer.Deserialize<WorkAssignmentReport>(historical), ct)).FieldValuesJson
                == sourcePayload.FieldValuesJson, "P05 historical source payload is preserved");
            await Unwritten("historical Approved no-fallback preview");
            artifacts.Add(new { scenario = "current-only-no-fallback", oldReportId = fixture.Source.Id, newReportId = newCurrent.Id, jobId = next.GetProperty("id").GetString() });
        }
        catch (Exception ex) { Console.Error.WriteLine("P05 current-only scenario failed before cleanup: " + ex); throw; }
        finally
        {
            // Retain the variant for audit, soft-deleted/non-current; restore the fixture's active occurrence atomically.
            var retired = currentDocument.DeepClone().AsBsonDocument;
            retired["isCurrent"] = false; retired["isDeleted"] = true; retired["isActive"] = false;
            // Release the unique current key before reinstating the original current report.
            await Restore(db, new[] { new Replacement(reports, currentDocument, currentDocument, retired) }.Concat(versionChanges));
        }
        var finalSource = await db.WorkAssignmentReports.Find(r => r.Id == fixture.Source.Id).SingleAsync(ct);
        var finalPeriod = await db.WorkReportPeriods.Find(p => p.Id == fixture.Source.WorkReportPeriodId).SingleAsync(ct);
        check(finalSource.IsCurrent && finalSource.Status == WorkAssignmentReportStatus.Approved
            && finalSource.PayloadRevision == fixture.Source.PayloadRevision && finalSource.PayloadHash == fixture.Source.PayloadHash
            && finalPeriod.CurrentReportId == finalSource.Id, "P05 failure-case cleanup restores original current source/period/payload");
        await Unwritten("all authority/version checks");
        await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/p05-authority-" + run + ".json",
            JsonSerializer.Serialize(new { run, state = "PASS", workId = fixture.WorkId, reportId = fixture.Report.Id,
                sourceReportIds = new[] { fixture.Source.Id, source2.Id }, variantReportId, variantState = "SOFT_DELETED_NON_CURRENT",
                scope = "HTTP_JOB_APPLY_WITH_CONTROLLED_FIXTURE_TRANSITIONS", artifacts }, new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    private static Task<BsonDocument> Document(IMongoCollection<BsonDocument> collection, string id, CancellationToken ct)
        => collection.Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync(ct);

    internal static async Task RestorePending(MongoDbContext db, string run, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(run, "^r8-text-20261005-[0-9a-f]{8}$"))
            throw new ArgumentException("Only the recorded own authority fixture run is supported");
        var path = "../outputs/aggregate-operators-20261005/p05-authority-backup-" + run + "-current-version.json";
        var text = await File.ReadAllTextAsync(path, ct);
        if ((await File.ReadAllTextAsync(path + ".sha256", ct)).Trim() != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))))
            throw new InvalidOperationException("Fixture backup hash mismatch");
        var backup = BsonDocument.Parse(text);
        if (backup["run"] != run || backup["scenario"] != "current-version") throw new InvalidOperationException("Backup scope mismatch");
        var changes = backup["documents"].AsBsonArray.Select(d => d.AsBsonDocument).Select(d => new Replacement(
            db.Db.GetCollection<BsonDocument>(d["collection"].AsString), d["before"].AsBsonDocument,
            d["during"].AsBsonDocument, d["after"].AsBsonDocument)).ToArray();
        if (changes.Length != 2 || changes[0].Collection.CollectionNamespace.CollectionName != db.WorkAssignmentReports.CollectionNamespace.CollectionName
            || changes[1].Collection.CollectionNamespace.CollectionName != db.WorkReportPeriods.CollectionNamespace.CollectionName)
            throw new InvalidOperationException("Unexpected restoration collections");
        var original = BsonSerializer.Deserialize<WorkAssignmentReport>(changes[0].Before);
        var work = await db.Works.Find(w => w.Id == original.WorkId).SingleAsync(ct);
        if (work.AutoCode != run || changes.Any(c => c.Before["workId"].AsObjectId.ToString() != work.Id))
            throw new InvalidOperationException("Restoration not confined to this synthetic work");
        var newId = changes[1].During["currentReportId"].AsObjectId.ToString();
        var reports = db.Db.GetCollection<BsonDocument>(db.WorkAssignmentReports.CollectionNamespace.CollectionName);
        var current = await Document(reports, newId, ct);
        var report = BsonSerializer.Deserialize<WorkAssignmentReport>(current);
        if (!report.IsCurrent || report.IsDeleted || report.WorkId != original.WorkId || report.WorkAssignmentId != original.WorkAssignmentId
            || report.WorkReportPeriodId != original.WorkReportPeriodId || report.AssigneeUserId != original.AssigneeUserId
            || report.DynamicFormTemplateId != original.DynamicFormTemplateId || report.DynamicFormSchemaHash != original.DynamicFormSchemaHash
            || report.DynamicFormVersionNo != original.DynamicFormVersionNo || report.VersionNo != original.VersionNo + 1
            || report.Status != WorkAssignmentReportStatus.Submitted) throw new InvalidOperationException("Unexpected variant report state");
        var payloads = new WorkReportPayloadService(db);
        var variantPayload = await payloads.LoadReportPayloadAsync(report, ct);
        var originalPayload = await payloads.LoadReportPayloadAsync(original, ct);
        if (variantPayload.FieldValuesJson != originalPayload.FieldValuesJson || variantPayload.TableValuesJson != originalPayload.TableValuesJson)
            throw new InvalidOperationException("Variant payload differs from copied source");
        await FixtureProvenanceChecks.Migration(db, "P05 before scoped fixture recovery", ct);
        var retired = current.DeepClone().AsBsonDocument;
        retired["isCurrent"] = false; retired["isDeleted"] = true; retired["isActive"] = false;
        var retirement = new Replacement(reports, current, current, retired);
        await Backup(run, "variant-retirement", [retirement], ct);
        await Restore(db, new[] { retirement }.Concat(changes));
        var active = await db.WorkAssignmentReports.Find(r => r.Id == original.Id).SingleAsync(ct);
        var period = await db.WorkReportPeriods.Find(p => p.Id == original.WorkReportPeriodId).SingleAsync(ct);
        if (!active.IsCurrent || active.Status != WorkAssignmentReportStatus.Approved || active.PayloadHash != original.PayloadHash
            || active.PayloadRevision != original.PayloadRevision || period.CurrentReportId != active.Id) throw new InvalidOperationException("Recovery readback mismatch");
        await FixtureProvenanceChecks.Migration(db, "P05 after scoped fixture recovery", ct);
        Console.WriteLine("PASS own fixture recovery work=" + work.Id + " restoredReport=" + active.Id + " retiredVariant=" + report.Id);
    }
    private static BsonDocument Exact(BsonDocument document)
        => new("$expr", new BsonDocument("$eq", new BsonArray { "$$ROOT", new BsonDocument("$literal", document) }));

    private static async Task Backup(string run, string scenario, IEnumerable<Replacement> changes, CancellationToken ct)
    {
        var path = "../outputs/aggregate-operators-20261005/p05-authority-backup-" + run + "-" + scenario + ".json";
        var text = new BsonDocument { ["run"] = run, ["scenario"] = scenario,
            ["documents"] = new BsonArray(changes.Select(c => new BsonDocument { ["collection"] = c.Collection.CollectionNamespace.CollectionName, ["before"] = c.Before, ["during"] = c.During, ["after"] = c.After })) }
            .ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson, Indent = true });
        await File.WriteAllTextAsync(path, text, ct);
        await File.WriteAllTextAsync(path + ".sha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), ct);
    }

    private static async Task Swap(MongoDbContext db, IEnumerable<Replacement> changes, bool restoring,
        CancellationToken ct, BsonDocument? insertedReport = null)
    {
        using var session = await db.Db.Client.StartSessionAsync(cancellationToken: ct);
        await session.WithTransactionAsync(async (transaction, token) =>
        {
            foreach (var replacement in changes)
            {
                var result = await replacement.Collection.ReplaceOneAsync(transaction,
                    Exact(restoring ? replacement.During : replacement.Before), restoring ? replacement.After : replacement.During,
                    cancellationToken: token);
                if (result.MatchedCount != 1) throw new InvalidOperationException("Fixture compare-and-set failed: " + replacement.Before["_id"]);
            }
            if (insertedReport != null) await db.Db.GetCollection<BsonDocument>(db.WorkAssignmentReports.CollectionNamespace.CollectionName)
                .InsertOneAsync(transaction, insertedReport, cancellationToken: token);
            return true;
        }, new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority), ct);
    }

    private static async Task Restore(MongoDbContext db, IEnumerable<Replacement> changes)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Swap(db, changes, true, timeout.Token);
    }
}
