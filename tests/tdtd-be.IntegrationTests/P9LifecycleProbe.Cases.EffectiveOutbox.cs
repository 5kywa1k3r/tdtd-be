using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task RunEffectiveCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-LFC-EFFECTIVE-01",
            async () =>
            {
                await AssertAllGenerationPinsAsync(ct);
                var assignment = await RequireDatabase()
                    .GetCollection<BsonDocument>("work_assignments")
                    .Find(new BsonDocument("_id", ObjectId.Parse(Fixture().AssignmentId)))
                    .SingleAsync(ct);
                var epoch = await RequireDatabase()
                    .GetCollection<BsonDocument>("dynamic_flow_execution_epochs")
                    .Find(new BsonDocument("_id", ObjectId.Parse(Fixture().FlowEpochId)))
                    .SingleAsync(ct);
                var step = await RequireDatabase()
                    .GetCollection<BsonDocument>("dynamic_flow_step_instances")
                    .Find(new BsonDocument("_id", ObjectId.Parse(Fixture().FlowStepInstanceId)))
                    .SingleAsync(ct);
                var flowVersion = await RequireDatabase()
                    .GetCollection<DynamicFlowTemplateVersion>(
                        "dynamic_flow_template_versions")
                    .Find(version => version.Id == Fixture().FlowVersionId)
                    .SingleAsync(ct);
                DynamicFlowContributionPolicyContract.ValidateLockedPolicy(
                    flowVersion);
                var report = await LoadLifecycleReportAsync(ct);
                HarnessAssert.Equal("EFFECTIVE", BsonString(assignment, "flowEffectiveStatus"), "Assignment effective state");
                HarnessAssert.Equal(1, BsonInt(assignment, "flowExecutionEpoch"), "Assignment execution epoch");
                HarnessAssert.True(BsonBool(epoch, "isCanonical"), "Flow epoch is not canonical");
                HarnessAssert.Equal("APPROVED", BsonString(step, "state"), "Flow step lifecycle state");
                HarnessAssert.Equal(3, BsonInt(step, "reportLifecycleRevision"), "Flow step lifecycle revision");
                HarnessAssert.Equal(
                    DynamicFlowContributionPolicyContract.Include,
                    flowVersion.ContributionPolicy,
                    "Locked Flow contribution policy");
                HarnessAssert.True(
                    IsCanonicalSha(flowVersion.ContributionPolicyHash),
                    "Locked Flow contribution policy hash");
                HarnessAssert.Equal(
                    DynamicFlowContributionPolicyContract.IncludeWarning,
                    flowVersion.ContributionWarning,
                    "Locked Flow contribution warning");
                HarnessAssert.Equal(
                    DynamicFlowContributionPolicyContract.Include,
                    BsonString(report, "cumulativeContributionMode"),
                    "Report contribution snapshot");
                return new CaseObservation(
                    "Every Direct row binds the one published generation, exact current Flow instance/epoch/step approved source and canonical locked INCLUDE policy.",
                    $"generation={_lfcGenerationId};epoch=1;step={Fixture().FlowStepInstanceId};policyHash={flowVersion.ContributionPolicyHash};stores=6");
            },
            ct);

        await RunCaseAsync(
            "P9-LFC-EFFECTIVE-02",
            async () =>
            {
                var assignments = RequireDatabase().GetCollection<BsonDocument>("work_assignments");
                await RunNegativeLifecycleVariantAsync(
                    "FLOW_NOT_EFFECTIVE",
                    async () => await assignments.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(Fixture().AssignmentId)),
                        Builders<BsonDocument>.Update.Set("flowEffectiveStatus", "INEFFECTIVE"),
                        cancellationToken: ct),
                    async () => await assignments.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(Fixture().AssignmentId)),
                        Builders<BsonDocument>.Update.Set("flowEffectiveStatus", "EFFECTIVE"),
                        cancellationToken: ct),
                    entryMutator: null,
                    ct);
                return new CaseObservation(
                    "An approved report outside the effective Flow path linked ZERO_WRITE and could not alter the prior published generation.",
                    $"variant=FLOW_NOT_EFFECTIVE;generation={_lfcGenerationId};delta=0");
            },
            ct);

        await RunCaseAsync(
            "P9-LFC-EFFECTIVE-03",
            async () =>
            {
                var fixture = Fixture();
                var assignments = RequireDatabase().GetCollection<BsonDocument>("work_assignments");
                var periods = RequireDatabase().GetCollection<BsonDocument>("work_report_periods");
                await RunNegativeLifecycleVariantAsync(
                    "PERIOD_CURRENT_REPORT_MISMATCH",
                    async () => await periods.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(fixture.ReportPeriodId)),
                        Builders<BsonDocument>.Update.Set("currentReportId", ObjectId.GenerateNewId()),
                        cancellationToken: ct),
                    async () => await periods.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(fixture.ReportPeriodId)),
                        Builders<BsonDocument>.Update.Set("currentReportId", ObjectId.Parse(fixture.ReportId)),
                        cancellationToken: ct),
                    null,
                    ct);
                await RunNegativeLifecycleVariantAsync(
                    "WORK_SCOPE_MISMATCH",
                    async () => await assignments.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(fixture.AssignmentId)),
                        Builders<BsonDocument>.Update.Set("workId", ObjectId.GenerateNewId()),
                        cancellationToken: ct),
                    async () => await assignments.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(fixture.AssignmentId)),
                        Builders<BsonDocument>.Update.Set("workId", ObjectId.Parse(fixture.WorkId)),
                        cancellationToken: ct),
                    null,
                    ct);
                await RunNegativeLifecycleVariantAsync(
                    "TENANT_SCOPE_MISMATCH",
                    async () => await assignments.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(fixture.AssignmentId)),
                        Builders<BsonDocument>.Update
                            .Set("issuedByUnitId", ObjectId.Parse(fixture.UnitBId))
                            .Set("targetUnitIds", new BsonArray { ObjectId.Parse(fixture.UnitBId) }),
                        cancellationToken: ct),
                    async () => await assignments.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(fixture.AssignmentId)),
                        Builders<BsonDocument>.Update
                            .Set("issuedByUnitId", ObjectId.Parse(Actor("admin").UnitId))
                            .Set("targetUnitIds", new BsonArray { ObjectId.Parse(fixture.UnitAId) }),
                        cancellationToken: ct),
                    null,
                    ct);
                return new CaseObservation(
                    "Wrong period, work scope and tenant scope each failed closed through the real worker with six-store plus publication-root delta zero.",
                    "variants=PERIOD_CURRENT_REPORT_MISMATCH,WORK_SCOPE_MISMATCH,TENANT_SCOPE_MISMATCH;writes=0");
            },
            ct);

        await RunCaseAsync(
            "P9-LFC-EFFECTIVE-04",
            async () =>
            {
                var fixture = Fixture();
                var templates = RequireDatabase().GetCollection<BsonDocument>("dynamic_form_templates");
                var reports = RequireDatabase().GetCollection<BsonDocument>("work_assignment_report");
                await RunNegativeLifecycleVariantAsync(
                    "LOCKED_CONFIG_MISSING",
                    async () => await templates.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(fixture.TemplateId)),
                        Builders<BsonDocument>.Update.Set("statisticConfigStatus", "DRAFT"),
                        cancellationToken: ct),
                    async () => await templates.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(fixture.TemplateId)),
                        Builders<BsonDocument>.Update.Set("statisticConfigStatus", "LOCKED"),
                        cancellationToken: ct),
                    null,
                    ct);
                await RunNegativeLifecycleVariantAsync(
                    "STALE_LIFECYCLE_REVISION",
                    async () => await reports.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(fixture.ReportId)),
                        Builders<BsonDocument>.Update.Set("lifecycleRevision", 4),
                        cancellationToken: ct),
                    async () => await reports.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(fixture.ReportId)),
                        Builders<BsonDocument>.Update.Set("lifecycleRevision", 3),
                        cancellationToken: ct),
                    null,
                    ct);
                await RunNegativeLifecycleVariantAsync(
                    "LIFECYCLE_OPERATION_SPOOF",
                    () => Task.CompletedTask,
                    () => Task.CompletedTask,
                    entry => entry["operation"] = "REVIEW_RETURN",
                    ct);
                await RunNegativeLifecycleVariantAsync(
                    "NON_CURRENT_SOURCE",
                    async () => await reports.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(fixture.ReportId)),
                        Builders<BsonDocument>.Update.Set("isCurrent", false),
                        cancellationToken: ct),
                    async () => await reports.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(fixture.ReportId)),
                        Builders<BsonDocument>.Update.Set("isCurrent", true),
                        cancellationToken: ct),
                    null,
                    ct);
                return new CaseObservation(
                    "Missing locked config, stale lifecycle, spoofed operation and non-current source all produced zero official or publication-root writes.",
                    "variants=LOCKED_CONFIG_MISSING,STALE_LIFECYCLE_REVISION,LIFECYCLE_OPERATION_SPOOF,NON_CURRENT_SOURCE;writes=0");
            },
            ct);
    }

    private async Task RunOutboxCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-LFC-OUTBOX-01",
            async () =>
            {
                var report = await LoadLifecycleReportAsync(ct);
                var entry = FindOutboxEntry(report, "REVIEW_APPROVE");
                HarnessAssert.Equal("COMPLETED", BsonString(entry, "state"), "Recovered approve entry state");
                HarnessAssert.Equal("PUBLISHED", BsonString(entry, "directProjectionState"), "Recovered approve Direct state");
                HarnessAssert.True(BsonInt(entry, "attemptCount") >= 2, "Outbox retry chronology lacks interruption+success attempts");
                HarnessAssert.True(_lfcQueueTrace.Any(item => item.Step == "APPROVE_POST_COMMIT_INTERRUPTED"), "Missing interrupted queue trace");
                HarnessAssert.True(_lfcQueueTrace.Any(item => item.Step == "CONCURRENT_WORKERS_CONVERGED"), "Missing convergence queue trace");
                return new CaseObservation(
                    "Queue trace proves durable pending, injected failure, retry claim and terminal Direct publication without losing the lifecycle intent.",
                    $"event={_lfcApproveEventKey};attempts={BsonInt(entry, "attemptCount")};terminal=PUBLISHED");
            },
            ct);

        await RunCaseAsync(
            "P9-LFC-OUTBOX-02",
            async () =>
            {
                var before = await CaptureLifecycleDirectSnapshotAsync(ct);
                await ClearCompletedDirectLinkAsync(ct);
                var worker = await ProcessLifecycleOutboxAsync(20, ct);
                HarnessAssert.True(ReadProcessed(worker) >= 1, "Bounded activation backfill did not process acknowledged approval");
                var report = await LoadLifecycleReportAsync(ct);
                var entry = FindOutboxEntry(report, "REVIEW_APPROVE");
                HarnessAssert.Equal("COMPLETED", BsonString(entry, "state"), "Backfill lifecycle state");
                HarnessAssert.Equal("PUBLISHED", BsonString(entry, "directProjectionState"), "Backfill Direct state");
                HarnessAssert.Equal(_lfcRunId, BsonString(entry, "directProjectionRunId"), "Backfill run replay identity");
                var after = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleSnapshotEqual(before, after, "acknowledged P8-era backfill replay");
                _lfcPublishedApproveEntry = (BsonDocument)entry.DeepClone();
                _lfcQueueTrace.Add(new P9LifecycleQueueEvidence(
                    "ACKNOWLEDGED_BACKFILL_REPLAY",
                    _lfcApproveEventKey!,
                    BsonString(entry, "state"),
                    BsonInt(entry, "attemptCount"),
                    BsonNullableString(entry, "lastError"),
                    _lfcRunId,
                    _lfcGenerationId,
                    DateTime.UtcNow));
                return new CaseObservation(
                    "Bounded activation backfill found a completed P8-era approval lacking Direct linkage and idempotently relinked the exact completed generation.",
                    $"processed={ReadProcessed(worker)};run={_lfcRunId};directHash={LifecycleSnapshotSha256(after)}");
            },
            ct);

        await RunCaseAsync(
            "P9-LFC-OUTBOX-03",
            async () =>
            {
                await CaptureLifecycleOwnerIndexEvidenceAsync(ct);
                var evidence = HarnessAssert.Required(
                    _lfcOwnerIndexEvidence,
                    "P9-LFC owner/index evidence");
                HarnessAssert.True(evidence.UniqueIndexesVerified, "Required Direct unique indexes were not verified");
                HarnessAssert.True(evidence.QueryIndexesVerified, "Required Direct query indexes were not verified");
                HarnessAssert.True(evidence.Explains.All(item => item.Passed), "A Direct explain assertion failed");
                HarnessAssert.True(evidence.CurrentPublicationVerified, "Current Direct publication fence was not verified");
                HarnessAssert.True(evidence.SourceRevisionFenceVerified, "Work source/publication revision fence was not verified");
                HarnessAssert.True(evidence.LegacyIsolationVerified, "Staged generation leaked into the legacy Direct slice");
                return new CaseObservation(
                    "Live Mongo inventory verified generation-prefixed uniqueness, one current publication, Work revision fencing, legacy-slice isolation and bounded IXSCAN/no-blocking-sort explains for all six stores.",
                    $"owners={evidence.Owners.Count};indexes={evidence.Inventory.Sum(owner => owner.Indexes.Count)};explains={evidence.Explains.Count};currentPublication=true;legacyIsolation=true");
            },
            ct);

        await RunCaseAsync(
            "P9-LFC-OUTBOX-04",
            async () =>
            {
                HarnessAssert.Equal(8, StatConfigPhaseBarrier.CurrentPhase, "Broad phase changed from P8");
                var before = await CaptureDatabaseSnapshotAsync(ct);
                var p8 = await RequireApi().GetAsync(
                    $"api/stat-config/bundle?ownerKind=DYNAMIC_FORM&ownerId={Fixture().TemplateId}",
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(p8, HttpStatusCode.OK, "P9-LFC P8 bundle regression GET");
                HarnessAssert.Equal(
                    "P8-BUNDLE-1",
                    p8.Json?["schemaVersion"]?.GetValue<string>(),
                    "P8 bundle schema");
                HarnessAssert.Equal(
                    "EMPTY_VALID",
                    p8.Json?["freshness"]?.GetValue<string>(),
                    "P8 empty bundle freshness");
                HarnessAssert.True(
                    p8.Json?["isEmpty"]?.GetValue<bool>() == true,
                    "P8 scoped empty bundle was not valid");
                var p10 = await RequireApi().PostAsync(
                    "api/stat-config/barriers/P10_RECONCILE",
                    new
                    {
                        ownerKind = "UNIT",
                        ownerId = Fixture().UnitAId,
                        commandId = "p9-lfc-p10-zero-write-020",
                        expectedBundleHash = Fixture().ConfigHash
                    },
                    Actor("admin").Token,
                    ct: ct);
                ExpectError(
                    p10,
                    HttpStatusCode.Conflict,
                    "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                    "P9-LFC P10 zero-write barrier");
                var after = await CaptureDatabaseSnapshotAsync(ct);
                HarnessAssert.Equal(SnapshotSha256(before), SnapshotSha256(after), "P8 GET/P10 barrier database snapshot");
                _lfcP8RegressionVerified = true;
                _lfcP10ZeroWriteVerified = true;
                return new CaseObservation(
                    "Focused canonical P8 bundle read stayed byte-stable and P10_RECONCILE remained stable 409 with exact full-database zero-write.",
                    $"phase=8;p8={(int)p8.StatusCode};p10={(int)p10.StatusCode};db={SnapshotSha256(after)}");
            },
            ct);
    }
}
