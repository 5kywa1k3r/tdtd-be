using System.Globalization;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsRun;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsRun;

// Typed internal discriminator; JSON remains the existing { reason, writes } details contract.
internal sealed record StatRunJobConflictDetails(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("writes")] int Writes);

public sealed partial class StatRunService : IStatRunService
{
    private static readonly IReadOnlySet<string> ScopeTypes = new HashSet<string>(
        ["WORK", "ROOT", "ASSIGNMENT"],
        StringComparer.Ordinal);

    private readonly MongoDbContext _ctx;
    private readonly IStatRunCandidateActivation _activation;
    private readonly IAppTimeService _time;
    private readonly int _maxRetryCount;

    public StatRunService(
        MongoDbContext ctx,
        IStatRunCandidateActivation activation,
        IAppTimeService time,
        IConfiguration configuration)
    {
        _ctx = ctx;
        _activation = activation;
        _time = time;
        _maxRetryCount = Math.Clamp(
            configuration.GetValue<int?>("DynamicFormStatisticRebuild:MaxRetryCount") ?? 5,
            1,
            20);
    }

    public async Task<StatRunCreateResult> CreateAsync(
        string capabilityId,
        StatRunCreateRequest request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        // Role and coarse work/scope authorization intentionally precede every
        // hidden source/config identifier and immutable-pin validation.
        RequireCommandRole(actor);
        var authorizedScope = NormalizeScopeForAuthorization(request, actor);
        await RequireCanCommandScopeAsync(authorizedScope, actor, ct);
        var normalized = NormalizeCreate(capabilityId, request, authorizedScope);

        var requestHash = StatRunCanonicalJson.HashObject(normalized);
        var tenantKey = NormalizeTenantKey(actor.UnitId);
        var receiptId = StatRunCanonicalJson.HashText(string.Join(
            "\n",
            "STAT_RUN_RECEIPT_V1",
            actor.Id,
            tenantKey,
            normalized.CapabilityId,
            normalized.CommandId));

        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == normalized.SourceReportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw HiddenSourceFailure(actor, "SOURCE_NOT_EFFECTIVE");
        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw HiddenSourceFailure(actor, "ASSIGNMENT_NOT_EFFECTIVE");
        RequireCanAccessSourceAssignment(normalized, assignment, actor);

        // Bind activation only after authorization against the exact
        // authoritative source assignment, and still before receipt lookup.
        var routeId = StatRunRouteRegistry.CoreJob(normalized.CapabilityId);
        var binding = _activation.RequireFoundation(normalized.CapabilityId, routeId);

        // Receipt existence and replay outcome are visible only after current
        // authorization on the exact authoritative source assignment.
        var existing = await _ctx.WorkReportStatisticRebuildJobs
            .Find(x => x.ReceiptId == receiptId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return await ResolveReplayAsync(existing, requestHash, actor, ct);

        if (!string.Equals(
                normalized.DynamicFormTemplateId,
                report.DynamicFormTemplateId,
                StringComparison.Ordinal) ||
            !string.Equals(
                assignment.DynamicFormTemplateId,
                report.DynamicFormTemplateId,
                StringComparison.Ordinal))
        {
            throw SourceConflict("SOURCE_SCOPE_MISMATCH");
        }
        var template = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == report.DynamicFormTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ConfigConflict("CONFIG_OWNER_NOT_EFFECTIVE");

        DynamicFlowTemplate? flowFamily = null;
        DynamicFlowTemplateVersion? flowVersion = null;
        DynamicFlowInstance? flowInstance = null;
        DynamicFlowExecutionEpoch? flowEpoch = null;
        DynamicFlowStepInstance? flowStep = null;
        if (!string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
        {
            if (!ObjectId.TryParse(assignment.FlowInstanceId, out _) ||
                !ObjectId.TryParse(assignment.FlowTemplateId, out _) ||
                assignment.FlowTemplateVersionNo is not > 0 ||
                assignment.FlowExecutionEpoch is not > 0 ||
                !ObjectId.TryParse(assignment.FlowBranchId, out _) ||
                string.IsNullOrWhiteSpace(assignment.FlowStepId) ||
                assignment.FlowStepId.Length > 128 ||
                assignment.FlowStepId.Any(char.IsControl) ||
                assignment.FlowAttemptNo is not > 0)
            {
                throw SourceConflict("FLOW_RUNTIME_PIN_INCOMPLETE");
            }
            flowFamily = await _ctx.DynamicFlowTemplates
                .Find(x => x.Id == assignment.FlowTemplateId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            flowVersion = await _ctx.DynamicFlowTemplateVersions
                .Find(x => x.TemplateId == assignment.FlowTemplateId &&
                           x.VersionNo == assignment.FlowTemplateVersionNo &&
                           x.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                           !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            flowInstance = await _ctx.DynamicFlowInstances
                .Find(x => x.Id == assignment.FlowInstanceId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            flowEpoch = await _ctx.DynamicFlowExecutionEpochs
                .Find(x => x.FlowInstanceId == assignment.FlowInstanceId &&
                           x.ExecutionEpoch == assignment.FlowExecutionEpoch &&
                           x.IsCanonical &&
                           !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            flowStep = await _ctx.DynamicFlowStepInstances
                .Find(x => x.FlowInstanceId == assignment.FlowInstanceId &&
                           x.ExecutionEpoch == assignment.FlowExecutionEpoch &&
                           x.FlowStepId == assignment.FlowStepId &&
                           x.BranchId == assignment.FlowBranchId &&
                           x.AttemptNo == assignment.FlowAttemptNo &&
                           x.IsCanonicalEpoch == true &&
                           x.InvalidatedByFlowEventId == null &&
                           !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (flowFamily is null || flowVersion is null ||
                flowInstance is null || flowEpoch is null || flowStep is null ||
                flowFamily.FamilyRevision < 1 ||
                !string.Equals(
                    flowFamily.Status,
                    DynamicFlowTemplateStatuses.Active,
                    StringComparison.Ordinal) ||
                !flowFamily.HasLockedVersion ||
                !StatRunCanonicalJson.IsCanonicalSha256(flowVersion.PayloadHash) ||
                string.IsNullOrWhiteSpace(flowVersion.CatalogVersion) ||
                !StatRunCanonicalJson.IsCanonicalSha256(flowVersion.CatalogSemanticHash) ||
                !string.Equals(
                    flowVersion.RootDynamicFormTemplateId,
                    report.DynamicFormTemplateId,
                    StringComparison.Ordinal) ||
                flowInstance.Revision < 1 ||
                !string.Equals(flowInstance.WorkId, assignment.WorkId, StringComparison.Ordinal) ||
                !string.Equals(flowInstance.FlowTemplateId, flowFamily.Id, StringComparison.Ordinal) ||
                !string.Equals(flowInstance.FlowTemplateVersionId, flowVersion.Id, StringComparison.Ordinal) ||
                flowInstance.FlowTemplateVersionNo != flowVersion.VersionNo ||
                !string.Equals(flowInstance.FlowPayloadHash, flowVersion.PayloadHash, StringComparison.Ordinal) ||
                !string.Equals(flowInstance.CatalogVersion, flowVersion.CatalogVersion, StringComparison.Ordinal) ||
                !string.Equals(
                    flowInstance.CatalogSemanticHash,
                    flowVersion.CatalogSemanticHash,
                    StringComparison.Ordinal) ||
                flowInstance.ExecutionEpoch != assignment.FlowExecutionEpoch ||
                flowInstance.State is not (
                    DynamicFlowInstanceStates.Active or
                    DynamicFlowInstanceStates.Reconciled or
                    DynamicFlowInstanceStates.Completed or
                    DynamicFlowInstanceStates.Finalized) ||
                flowEpoch.Revision < 1 ||
                flowEpoch.State is not (
                    DynamicFlowExecutionEpochStates.Active or
                    DynamicFlowExecutionEpochStates.Finalized) ||
                flowStep.Revision < 1 ||
                flowStep.State is not (
                    DynamicFlowStepStates.Approved or
                    DynamicFlowStepStates.Completed) ||
                flowStep.InvalidatedAtUtc is not null ||
                flowStep.InvalidatedByFlowEventId is not null ||
                flowStep.SupersededByStepInstanceId is not null ||
                !string.Equals(flowStep.AssignmentId, assignment.Id, StringComparison.Ordinal) ||
                !string.Equals(flowStep.ReportId, report.Id, StringComparison.Ordinal) ||
                flowStep.ReportLifecycleRevision != report.LifecycleRevision ||
                !string.Equals(
                    flowStep.ReportLifecycleStatus,
                    report.Status.ToString().ToUpperInvariant(),
                    StringComparison.Ordinal) ||
                flowStep.ReportLifecycleIsActive != report.IsActive ||
                !StatRunCanonicalJson.IsCanonicalSha256(report.DynamicFormSchemaHash) ||
                !StatRunCanonicalJson.IsCanonicalSha256(flowStep.FormSchemaHash) ||
                !string.Equals(
                    report.DynamicFormFamilyId,
                    flowStep.FormFamilyId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    report.DynamicFormTemplateId,
                    flowStep.FormVersionId,
                    StringComparison.Ordinal) ||
                report.DynamicFormVersionNo != flowStep.FormVersionNo ||
                !string.Equals(
                    report.DynamicFormSchemaHash,
                    flowStep.FormSchemaHash,
                    StringComparison.Ordinal))
            {
                throw SourceConflict("FLOW_RUNTIME_PIN_NOT_EFFECTIVE");
            }
        }

        ValidateAuthoritativePins(normalized, report, assignment, template);

        var now = MongoUtcNow();
        var initialDeadline = now.AddMinutes(10);
        var jobId = ObjectId.GenerateNewId().ToString();
        var jobDedupeKey = BuildJobDedupeKey(
            normalized,
            actor,
            binding,
            report,
            assignment,
            template,
            flowFamily,
            flowVersion,
            flowInstance,
            flowEpoch,
            flowStep);
        var stateRevision = 1L;
        var resetReceiptHistoryHash = BuildResetReceiptHistoryHash(
            Array.Empty<WorkReportStatisticRebuildJobResetReceipt>());
        var stateHash = BuildStateHash(
            jobId,
            WorkReportStatisticRebuildJobStatuses.Pending,
            stateRevision,
            0,
            now,
            null,
            initialDeadline,
            null,
            null,
            null,
            null,
            null,
            resetReceiptHistoryHash,
            WorkReportStatisticRebuildJobFreshnessStates.Pending,
            null);

        var job = new WorkReportStatisticRebuildJob
        {
            Id = jobId,
            DedupeKey = jobDedupeKey,
            ReceiptId = receiptId,
            CommandId = normalized.CommandId,
            RequestHash = requestHash,
            ReceiptAcceptedAtUtc = now,
            CapabilityId = normalized.CapabilityId,
            RouteId = routeId,
            RunKind = WorkReportStatisticRebuildJobRunKinds.Foundation,
            ActorUserId = actor.Id,
            TenantUnitId = ObjectId.TryParse(actor.UnitId, out var unitId) ? unitId.ToString() : null,
            ScopeType = normalized.ScopeType,
            ScopeId = normalized.ScopeId,
            SourceReportId = report.Id,
            SourcePayloadRevision = report.PayloadRevision,
            SourcePayloadHash = report.PayloadHash,
            SourceLifecycleRevision = report.LifecycleRevision,
            SourceStatus = report.Status.ToString().ToUpperInvariant(),
            ConfigId = template.StatisticConfigId,
            ConfigVersionId = template.StatisticConfigVersionId,
            ConfigVersionNo = template.StatisticConfigVersionNo,
            ConfigRevision = template.StatisticConfigRevision,
            ConfigHash = template.StatisticConfigHash,
            CatalogVersion = binding.CatalogVersion,
            CatalogRawSha256 = binding.CatalogRawSha256,
            CatalogSemanticSha256 = binding.CatalogSemanticSha256,
            SchemaRawSha256 = binding.SchemaRawSha256,
            SchemaSemanticSha256 = binding.SchemaSemanticSha256,
            StageLockSha256 = binding.StageLockSha256,
            CandidateChainId = binding.ChainId,
            DynamicFormTemplateId = template.Id,
            DynamicFormTemplateCode = template.Code,
            DynamicFormTemplateName = template.Name,
            ScopeKind = WorkReportStatisticRebuildJobScopeKinds.Bounded,
            WorkId = report.WorkId,
            WorkAssignmentId = report.WorkAssignmentId,
            FlowInstanceId = assignment.FlowInstanceId,
            FlowInstanceRevision = flowInstance?.Revision,
            FlowInstanceState = flowInstance?.State,
            FlowEffectiveStatus = assignment.FlowEffectiveStatus,
            FlowTemplateId = assignment.FlowTemplateId,
            FlowFamilyRevision = flowFamily?.FamilyRevision,
            FlowTemplateVersionNo = assignment.FlowTemplateVersionNo,
            FlowTemplateVersionId = flowVersion?.Id,
            FlowPayloadHash = flowVersion?.PayloadHash,
            FlowCatalogVersion = flowVersion?.CatalogVersion,
            FlowCatalogSemanticHash = flowVersion?.CatalogSemanticHash,
            FlowExecutionEpoch = assignment.FlowExecutionEpoch,
            FlowExecutionEpochId = flowEpoch?.Id,
            FlowExecutionEpochRevision = flowEpoch?.Revision,
            FlowExecutionEpochState = flowEpoch?.State,
            FlowBranchId = assignment.FlowBranchId,
            FlowStepId = assignment.FlowStepId,
            FlowAttemptNo = assignment.FlowAttemptNo,
            FlowStepInstanceId = flowStep?.Id,
            FlowStepInstanceRevision = flowStep?.Revision,
            FlowStepInstanceState = flowStep?.State,
            PeriodKey = report.PeriodKey,
            PeriodInstanceKey = report.PeriodInstanceKey,
            PeriodKind = report.PeriodKind,
            PeriodStartUtc = NormalizeUtc(report.PeriodStart),
            PeriodEndUtc = NormalizeUtc(report.PeriodEnd),
            Status = WorkReportStatisticRebuildJobStatuses.Pending,
            StateRevision = stateRevision,
            StateHash = stateHash,
            IsActive = true,
            RequestedByUserId = actor.Id,
            Priority = WorkReportStatisticRebuildJobPriorities.High,
            TotalReportCount = 1,
            ProcessedReportCount = 0,
            FailedReportCount = 0,
            RetryCount = 0,
            NextRetryAtUtc = now,
            DeadlineAtUtc = initialDeadline,
            InitialDeadlineAtUtc = initialDeadline,
            FreshnessState = WorkReportStatisticRebuildJobFreshnessStates.Pending,
            ResetReceiptHistoryHash = resetReceiptHistoryHash,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
            UpdatedByUserId = actor.Id,
            IsDeleted = false
        };
        job.ImmutableHeaderHash = BuildImmutableHeaderHash(job);
        job.ReceiptResponseHash = BuildAcceptedResponseHash(job);

        try
        {
            await _ctx.WorkReportStatisticRebuildJobs.InsertOneAsync(job, cancellationToken: ct);
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            existing = await _ctx.WorkReportStatisticRebuildJobs
                .Find(x => x.ReceiptId == receiptId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
                return await ResolveReplayAsync(existing, requestHash, actor, ct);

            var activeCanonicalCollision = await _ctx.WorkReportStatisticRebuildJobs
                .Find(x => x.DedupeKey == jobDedupeKey &&
                           x.IsActive &&
                           !x.IsDeleted &&
                           x.RunKind == WorkReportStatisticRebuildJobRunKinds.Foundation &&
                           x.ReceiptId != null &&
                           x.ReceiptId != string.Empty &&
                           x.CandidateChainId == StatRunCapabilityActivation.RequiredChainId)
                .Limit(1)
                .AnyAsync(ct);
            if (activeCanonicalCollision)
                throw JobConflict("ACTIVE_RUN_ALREADY_EXISTS");
            throw JobConflict("DUPLICATE_KEY_INTEGRITY_CONFLICT");
        }

        return new StatRunCreateResult(false, await ToResponseAsync(job, false, ct));
    }

    public async Task<StatRunJobResponse> GetAsync(
        string jobId,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RequireCommandRole(actor);
        // A scoped caller must not learn whether a malformed identifier differs
        // from a missing or hidden job. SYSTEM_ADMIN keeps actionable syntax
        // validation; every other actor receives the same authorization result.
        if (!RoleGuard.IsSystemAdmin(actor) &&
            !ObjectId.TryParse(jobId?.Trim(), out _))
        {
            throw Forbidden();
        }
        jobId = NormalizeObjectId(jobId, "jobId");
        FilterDefinition<WorkReportStatisticRebuildJob> filter =
            Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.Id, jobId) &
            Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.IsDeleted, false) &
            Builders<WorkReportStatisticRebuildJob>.Filter.Eq(
                x => x.RunKind,
                WorkReportStatisticRebuildJobRunKinds.Foundation) &
            Builders<WorkReportStatisticRebuildJob>.Filter.Ne(x => x.ReceiptId, null) &
            Builders<WorkReportStatisticRebuildJob>.Filter.Ne(x => x.ReceiptId, string.Empty) &
            Builders<WorkReportStatisticRebuildJob>.Filter.Eq(
                x => x.CandidateChainId,
                StatRunCapabilityActivation.RequiredChainId);
        if (!RoleGuard.IsSystemAdmin(actor))
        {
            if (!ObjectId.TryParse(actor.UnitId, out var unitId))
                throw Forbidden();
            filter &= Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.ActorUserId, actor.Id);
            filter &= Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.TenantUnitId, unitId.ToString());
        }

        var job = await _ctx.WorkReportStatisticRebuildJobs
            .Find(filter)
            .FirstOrDefaultAsync(ct)
            ?? throw Forbidden();
        await RequireCanReadJobScopeAsync(job, actor, ct);
        RequireJobActivation(job);
        job = await RefreshExpiredJobAsync(job, actor, ct);
        return await ToResponseAsync(job, false, ct);
    }

    private async Task<StatRunCreateResult> ResolveReplayAsync(
        WorkReportStatisticRebuildJob existing,
        string requestHash,
        MeResponse actor,
        CancellationToken ct)
    {
        if (!string.Equals(existing.ActorUserId, actor.Id, StringComparison.Ordinal) ||
            !string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            throw new AppException(
                AppErrorCode.STAT_RUN_COMMAND_REPLAY_MISMATCH,
                new { reason = "REQUEST_HASH_MISMATCH", writes = 0 });
        }
        RequireJobActivation(existing);
        existing = await RefreshExpiredJobAsync(existing, actor, ct);

        return new StatRunCreateResult(true, await ToResponseAsync(existing, true, ct));
    }

    private async Task RequireCanReadJobScopeAsync(
        WorkReportStatisticRebuildJob job,
        MeResponse actor,
        CancellationToken ct)
    {
        RequireCommandRole(actor);
        if (RoleGuard.IsSystemAdmin(actor))
            return;
        if (!ObjectId.TryParse(actor.UnitId, out var actorUnitId))
            throw Forbidden();

        var fb = Builders<WorkAssignment>.Filter;
        var filter = fb.Eq(x => x.Id, job.WorkAssignmentId) &
                     fb.Eq(x => x.WorkId, job.WorkId) &
                     fb.Eq(x => x.IsDeleted, false) &
                     fb.Eq(x => x.IsActive, true) &
                     (fb.Eq(x => x.CreatedByUserId, actor.Id) |
                      fb.AnyEq(x => x.LeaderWatcherUserIds, actor.Id)) &
                     (fb.Eq(x => x.IssuedByUnitId, actorUnitId.ToString()) |
                      fb.AnyEq(x => x.TargetUnitIds!, actorUnitId.ToString()));
        if (job.ScopeType == "ROOT")
            filter &= fb.Eq(x => x.RootAssignmentId, job.ScopeId);
        else if (job.ScopeType == "ASSIGNMENT")
            filter &= fb.Eq(x => x.Id, job.ScopeId);
        if (!await _ctx.WorkAssignments.Find(filter).Limit(1).AnyAsync(ct))
            throw Forbidden();
    }

    private static void RequireCanAccessSourceAssignment(
        NormalizedCreate request,
        WorkAssignment assignment,
        MeResponse actor)
    {
        if (RoleGuard.IsSystemAdmin(actor))
            return;
        if (!ObjectId.TryParse(actor.UnitId, out var actorUnitId))
            throw Forbidden();

        var actorOwnsSource =
            string.Equals(assignment.CreatedByUserId, actor.Id, StringComparison.Ordinal) ||
            (assignment.LeaderWatcherUserIds?.Contains(actor.Id, StringComparer.Ordinal) ?? false);
        var tenantOwnsSource =
            string.Equals(assignment.IssuedByUnitId, actorUnitId.ToString(), StringComparison.Ordinal) ||
            (assignment.TargetUnitIds?.Contains(actorUnitId.ToString(), StringComparer.Ordinal) ?? false);
        var scopeMatches =
            string.Equals(assignment.WorkId, request.WorkId, StringComparison.Ordinal) &&
            (request.ScopeType switch
            {
                "WORK" => true,
                "ROOT" => string.Equals(
                    assignment.RootAssignmentId,
                    request.ScopeId,
                    StringComparison.Ordinal),
                "ASSIGNMENT" => string.Equals(
                    assignment.Id,
                    request.ScopeId,
                    StringComparison.Ordinal),
                _ => false
            });
        if (!actorOwnsSource || !tenantOwnsSource || !scopeMatches)
            throw Forbidden();
    }

    private async Task RequireCanCommandScopeAsync(
        NormalizedCommandScope request,
        MeResponse actor,
        CancellationToken ct)
    {
        RequireCommandRole(actor);
        if (RoleGuard.IsSystemAdmin(actor))
            return;

        if (!ObjectId.TryParse(actor.UnitId, out var actorUnitId))
            throw Forbidden();

        var fb = Builders<WorkAssignment>.Filter;
        var actorFilter = fb.Eq(x => x.CreatedByUserId, actor.Id) |
                          fb.AnyEq(x => x.LeaderWatcherUserIds, actor.Id);
        var tenantFilter = fb.Eq(x => x.IssuedByUnitId, actorUnitId.ToString()) |
                           fb.AnyEq(x => x.TargetUnitIds!, actorUnitId.ToString());
        var filter = fb.Eq(x => x.WorkId, request.WorkId) &
                     fb.Eq(x => x.IsDeleted, false) &
                     fb.Eq(x => x.IsActive, true) &
                     actorFilter &
                     tenantFilter;
        switch (request.ScopeType)
        {
            case "ROOT":
                filter &= fb.Eq(x => x.RootAssignmentId, request.ScopeId);
                break;
            case "ASSIGNMENT":
                filter &= fb.Eq(x => x.Id, request.ScopeId);
                break;
        }

        var authorized = await _ctx.WorkAssignments.Find(filter).Limit(1).AnyAsync(ct);
        if (!authorized)
            throw Forbidden();
    }

    private static void ValidateAuthoritativePins(
        NormalizedCreate request,
        WorkAssignmentReport report,
        WorkAssignment assignment,
        DynamicFormTemplate template)
    {
        if (!string.Equals(report.WorkId, request.WorkId, StringComparison.Ordinal) ||
            !string.Equals(report.DynamicFormTemplateId, request.DynamicFormTemplateId, StringComparison.Ordinal) ||
            !string.Equals(assignment.WorkId, request.WorkId, StringComparison.Ordinal) ||
            !string.Equals(assignment.Id, report.WorkAssignmentId, StringComparison.Ordinal) ||
            !string.Equals(assignment.DynamicFormTemplateId, request.DynamicFormTemplateId, StringComparison.Ordinal))
        {
            throw SourceConflict("SOURCE_SCOPE_MISMATCH");
        }

        if (request.ScopeType == "ASSIGNMENT" &&
            !string.Equals(report.WorkAssignmentId, request.ScopeId, StringComparison.Ordinal))
        {
            throw SourceConflict("SOURCE_SCOPE_MISMATCH");
        }
        if (request.ScopeType == "ROOT" &&
            !string.Equals(assignment.RootAssignmentId, request.ScopeId, StringComparison.Ordinal))
        {
            throw SourceConflict("SOURCE_SCOPE_MISMATCH");
        }

        if (report.Status != WorkAssignmentReportStatus.Approved)
            throw new AppException(AppErrorCode.STAT_RUN_SOURCE_NOT_APPROVED, new { writes = 0 });
        if (!report.IsCurrent || !report.IsActive || !string.IsNullOrWhiteSpace(report.InvalidatedByFlowEventId) ||
            !assignment.IsActive ||
            !string.IsNullOrWhiteSpace(assignment.InvalidatedByFlowEventId) ||
            (!string.IsNullOrWhiteSpace(assignment.FlowInstanceId) &&
             !string.Equals(assignment.FlowEffectiveStatus, "EFFECTIVE", StringComparison.Ordinal)))
        {
            throw SourceConflict("SOURCE_NOT_EFFECTIVE");
        }

        if (report.PayloadRevision != request.ExpectedSourceRevision ||
            report.LifecycleRevision != request.ExpectedLifecycleRevision ||
            !string.Equals(report.PayloadHash, request.ExpectedSourceHash, StringComparison.Ordinal))
        {
            throw new AppException(
                AppErrorCode.STAT_RUN_REVISION_CONFLICT,
                new { reason = "SOURCE_PIN_STALE", writes = 0 });
        }

        if (!string.Equals(report.PeriodKey, request.PeriodKey, StringComparison.Ordinal) ||
            !string.Equals(report.PeriodInstanceKey, request.PeriodInstanceKey, StringComparison.Ordinal) ||
            !string.Equals(report.PeriodKind, request.PeriodKind, StringComparison.Ordinal) ||
            NormalizeUtc(report.PeriodStart) != request.PeriodStartUtc ||
            NormalizeUtc(report.PeriodEnd) != request.PeriodEndUtc)
        {
            throw new AppException(
                AppErrorCode.STAT_RUN_REVISION_CONFLICT,
                new { reason = "PERIOD_PIN_STALE", writes = 0 });
        }

        if (!string.Equals(template.StatisticConfigStatus, "LOCKED", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(template.StatisticConfigId) ||
            string.IsNullOrWhiteSpace(template.StatisticConfigVersionId) ||
            template.StatisticConfigVersionNo < 1 ||
            template.StatisticConfigRevision != request.ExpectedConfigRevision ||
            !string.Equals(template.StatisticConfigHash, request.ExpectedConfigHash, StringComparison.Ordinal))
        {
            throw ConfigConflict("LOCKED_CONFIG_PIN_STALE");
        }
    }

    private async Task<StatRunJobResponse> ToResponseAsync(
        WorkReportStatisticRebuildJob job,
        bool isReplay,
        CancellationToken ct)
    {
        var (freshness, staleReason) = await ResolveFreshnessAsync(job, ct);
        var projectionRunId = await ResolveFoundationProjectionRunIdAsync(job, ct);
        return new StatRunJobResponse
        {
            JobId = job.Id,
            RunId = job.Id,
            ReceiptId = job.ReceiptId ?? string.Empty,
            CommandId = job.CommandId ?? string.Empty,
            CapabilityId = job.CapabilityId ?? string.Empty,
            RunKind = job.RunKind ?? string.Empty,
            Status = ToApiStatus(job.Status),
            StateRevision = job.StateRevision,
            StateHash = job.StateHash ?? string.Empty,
            IsReplay = isReplay,
            WorkId = job.WorkId ?? string.Empty,
            ScopeType = job.ScopeType ?? string.Empty,
            ScopeId = job.ScopeId,
            SourceReportId = job.SourceReportId ?? string.Empty,
            SourceRevision = job.SourcePayloadRevision ?? 0,
            SourceHash = job.SourcePayloadHash ?? string.Empty,
            LifecycleRevision = job.SourceLifecycleRevision ?? 0,
            DynamicFormTemplateId = job.DynamicFormTemplateId,
            ConfigId = job.ConfigId ?? string.Empty,
            ConfigVersionId = job.ConfigVersionId ?? string.Empty,
            ConfigVersionNo = job.ConfigVersionNo ?? 0,
            ConfigRevision = job.ConfigRevision ?? 0,
            ConfigHash = job.ConfigHash ?? string.Empty,
            CatalogVersion = job.CatalogVersion ?? string.Empty,
            CatalogRawSha256 = job.CatalogRawSha256 ?? string.Empty,
            CatalogSemanticSha256 = job.CatalogSemanticSha256 ?? string.Empty,
            SchemaRawSha256 = job.SchemaRawSha256 ?? string.Empty,
            SchemaSemanticSha256 = job.SchemaSemanticSha256 ?? string.Empty,
            StageLockSha256 = job.StageLockSha256 ?? string.Empty,
            CandidateChainId = job.CandidateChainId ?? string.Empty,
            FlowTemplateId = job.FlowTemplateId,
            FlowFamilyRevision = job.FlowFamilyRevision,
            FlowTemplateVersionNo = job.FlowTemplateVersionNo,
            FlowTemplateVersionId = job.FlowTemplateVersionId,
            FlowPayloadHash = job.FlowPayloadHash,
            FlowCatalogVersion = job.FlowCatalogVersion,
            FlowCatalogSemanticHash = job.FlowCatalogSemanticHash,
            FlowInstanceId = job.FlowInstanceId,
            FlowInstanceRevision = job.FlowInstanceRevision,
            FlowInstanceState = job.FlowInstanceState,
            FlowExecutionEpoch = job.FlowExecutionEpoch,
            FlowExecutionEpochId = job.FlowExecutionEpochId,
            FlowExecutionEpochRevision = job.FlowExecutionEpochRevision,
            FlowExecutionEpochState = job.FlowExecutionEpochState,
            FlowBranchId = job.FlowBranchId,
            FlowStepId = job.FlowStepId,
            FlowAttemptNo = job.FlowAttemptNo,
            FlowStepInstanceId = job.FlowStepInstanceId,
            FlowStepInstanceRevision = job.FlowStepInstanceRevision,
            FlowStepInstanceState = job.FlowStepInstanceState,
            PeriodKey = job.PeriodKey ?? string.Empty,
            PeriodInstanceKey = job.PeriodInstanceKey ?? string.Empty,
            PeriodKind = job.PeriodKind ?? string.Empty,
            PeriodStartUtc = job.PeriodStartUtc,
            PeriodEndUtc = job.PeriodEndUtc,
            RetryCount = job.RetryCount,
            NextRetryAtUtc = job.NextRetryAtUtc,
            LeaseUntilUtc = job.LeaseUntilUtc,
            LastHeartbeatAtUtc = job.LastHeartbeatAtUtc,
            DeadlineAtUtc = job.DeadlineAtUtc,
            StartedAtUtc = job.LastRunAtUtc,
            CompletedAtUtc = job.CompletedAtUtc,
            ComputedAtUtc = job.ComputedAtUtc,
            GenerationId = job.GenerationId,
            GenerationHash = job.GenerationHash,
            ProjectionRunId = projectionRunId,
            FreshnessState = freshness,
            StaleReason = staleReason,
            DiagnosticCode = RedactDiagnostic(job.DiagnosticCode),
            CreatedAtUtc = job.CreatedAtUtc,
            UpdatedAtUtc = job.UpdatedAtUtc
        };
    }

    private async Task<(string State, string? Reason)> ResolveFreshnessAsync(
        WorkReportStatisticRebuildJob job,
        CancellationToken ct)
    {
        if (!string.Equals(job.Status, WorkReportStatisticRebuildJobStatuses.Completed, StringComparison.Ordinal))
            return (WorkReportStatisticRebuildJobFreshnessStates.Pending, null);

        var report = string.IsNullOrWhiteSpace(job.SourceReportId)
            ? null
            : await _ctx.WorkAssignmentReports
                .Find(x => x.Id == job.SourceReportId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
        if (report is null || report.Status != WorkAssignmentReportStatus.Approved ||
            !report.IsCurrent || !report.IsActive || !string.IsNullOrWhiteSpace(report.InvalidatedByFlowEventId))
        {
            return (WorkReportStatisticRebuildJobFreshnessStates.Stale, "SOURCE_NOT_EFFECTIVE");
        }
        if (report.PayloadRevision != job.SourcePayloadRevision ||
            report.LifecycleRevision != job.SourceLifecycleRevision ||
            !string.Equals(report.WorkId, job.WorkId, StringComparison.Ordinal) ||
            !string.Equals(report.WorkAssignmentId, job.WorkAssignmentId, StringComparison.Ordinal) ||
            !string.Equals(report.DynamicFormTemplateId, job.DynamicFormTemplateId, StringComparison.Ordinal) ||
            !string.Equals(report.PayloadHash, job.SourcePayloadHash, StringComparison.Ordinal) ||
            !string.Equals(report.PeriodKey, job.PeriodKey, StringComparison.Ordinal) ||
            !string.Equals(report.PeriodInstanceKey, job.PeriodInstanceKey, StringComparison.Ordinal) ||
            !string.Equals(report.PeriodKind, job.PeriodKind, StringComparison.Ordinal) ||
            NormalizeUtc(report.PeriodStart) != job.PeriodStartUtc ||
            NormalizeUtc(report.PeriodEnd) != job.PeriodEndUtc)
        {
            return (WorkReportStatisticRebuildJobFreshnessStates.Stale, "SOURCE_REVISION_CHANGED");
        }

        var template = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == job.DynamicFormTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (template is null ||
            !string.Equals(template.StatisticConfigStatus, "LOCKED", StringComparison.Ordinal) ||
            !string.Equals(template.StatisticConfigId, job.ConfigId, StringComparison.Ordinal) ||
            template.StatisticConfigVersionNo != job.ConfigVersionNo ||
            template.StatisticConfigRevision != job.ConfigRevision ||
            !string.Equals(template.StatisticConfigHash, job.ConfigHash, StringComparison.Ordinal) ||
            !string.Equals(template.StatisticConfigVersionId, job.ConfigVersionId, StringComparison.Ordinal))
        {
            return (WorkReportStatisticRebuildJobFreshnessStates.Stale, "CONFIG_CHANGED");
        }

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == job.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (assignment is null || !assignment.IsActive ||
            !string.IsNullOrWhiteSpace(assignment.InvalidatedByFlowEventId) ||
            !string.Equals(assignment.WorkId, job.WorkId, StringComparison.Ordinal) ||
            !string.Equals(assignment.DynamicFormTemplateId, job.DynamicFormTemplateId, StringComparison.Ordinal) ||
            (job.ScopeType == "ASSIGNMENT" &&
             !string.Equals(assignment.Id, job.ScopeId, StringComparison.Ordinal)) ||
            (job.ScopeType == "ROOT" &&
             !string.Equals(assignment.RootAssignmentId, job.ScopeId, StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(assignment.FlowInstanceId) &&
             !string.Equals(assignment.FlowEffectiveStatus, "EFFECTIVE", StringComparison.Ordinal)) ||
            !string.Equals(assignment.FlowTemplateId, job.FlowTemplateId, StringComparison.Ordinal) ||
            assignment.FlowTemplateVersionNo != job.FlowTemplateVersionNo ||
            assignment.FlowExecutionEpoch != job.FlowExecutionEpoch ||
            !string.Equals(assignment.FlowInstanceId, job.FlowInstanceId, StringComparison.Ordinal) ||
            !string.Equals(assignment.FlowBranchId, job.FlowBranchId, StringComparison.Ordinal) ||
            !string.Equals(assignment.FlowStepId, job.FlowStepId, StringComparison.Ordinal) ||
            assignment.FlowAttemptNo != job.FlowAttemptNo)
        {
            return (WorkReportStatisticRebuildJobFreshnessStates.Stale, "RUNTIME_CHANGED");
        }

        if (!string.IsNullOrWhiteSpace(job.FlowInstanceId))
        {
            var flowFamily = await _ctx.DynamicFlowTemplates
                .Find(x => x.Id == job.FlowTemplateId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            var flowVersion = await _ctx.DynamicFlowTemplateVersions
                .Find(x => x.Id == job.FlowTemplateVersionId &&
                           x.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                           !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            var flowInstance = await _ctx.DynamicFlowInstances
                .Find(x => x.Id == job.FlowInstanceId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            var flowEpoch = await _ctx.DynamicFlowExecutionEpochs
                .Find(x => x.Id == job.FlowExecutionEpochId &&
                           x.IsCanonical &&
                           !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            var flowStep = await _ctx.DynamicFlowStepInstances
                .Find(x => x.Id == job.FlowStepInstanceId &&
                           x.IsCanonicalEpoch == true &&
                           x.InvalidatedByFlowEventId == null &&
                           !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (flowFamily is null || flowVersion is null ||
                flowInstance is null || flowEpoch is null || flowStep is null ||
                flowFamily.FamilyRevision != job.FlowFamilyRevision ||
                flowVersion.VersionNo != job.FlowTemplateVersionNo ||
                !string.Equals(flowVersion.TemplateId, job.FlowTemplateId, StringComparison.Ordinal) ||
                !string.Equals(flowVersion.PayloadHash, job.FlowPayloadHash, StringComparison.Ordinal) ||
                !string.Equals(flowVersion.CatalogVersion, job.FlowCatalogVersion, StringComparison.Ordinal) ||
                !string.Equals(flowVersion.CatalogSemanticHash, job.FlowCatalogSemanticHash, StringComparison.Ordinal) ||
                flowInstance.Revision != job.FlowInstanceRevision ||
                !string.Equals(flowInstance.State, job.FlowInstanceState, StringComparison.Ordinal) ||
                !string.Equals(flowInstance.WorkId, job.WorkId, StringComparison.Ordinal) ||
                !string.Equals(flowInstance.FlowTemplateId, job.FlowTemplateId, StringComparison.Ordinal) ||
                !string.Equals(flowInstance.FlowTemplateVersionId, job.FlowTemplateVersionId, StringComparison.Ordinal) ||
                flowInstance.FlowTemplateVersionNo != job.FlowTemplateVersionNo ||
                !string.Equals(flowInstance.FlowPayloadHash, job.FlowPayloadHash, StringComparison.Ordinal) ||
                !string.Equals(flowInstance.CatalogVersion, job.FlowCatalogVersion, StringComparison.Ordinal) ||
                !string.Equals(
                    flowInstance.CatalogSemanticHash,
                    job.FlowCatalogSemanticHash,
                    StringComparison.Ordinal) ||
                flowInstance.ExecutionEpoch != job.FlowExecutionEpoch ||
                flowEpoch.Revision != job.FlowExecutionEpochRevision ||
                !string.Equals(flowEpoch.State, job.FlowExecutionEpochState, StringComparison.Ordinal) ||
                !string.Equals(flowEpoch.FlowInstanceId, job.FlowInstanceId, StringComparison.Ordinal) ||
                flowEpoch.ExecutionEpoch != job.FlowExecutionEpoch ||
                flowStep.Revision != job.FlowStepInstanceRevision ||
                !string.Equals(flowStep.State, job.FlowStepInstanceState, StringComparison.Ordinal) ||
                !string.Equals(flowStep.FlowInstanceId, job.FlowInstanceId, StringComparison.Ordinal) ||
                flowStep.ExecutionEpoch != job.FlowExecutionEpoch ||
                !string.Equals(flowStep.FlowStepId, job.FlowStepId, StringComparison.Ordinal) ||
                !string.Equals(flowStep.BranchId, job.FlowBranchId, StringComparison.Ordinal) ||
                flowStep.AttemptNo != job.FlowAttemptNo)
            {
                return (WorkReportStatisticRebuildJobFreshnessStates.Stale, "FLOW_VERSION_CHANGED");
            }
        }

        return (WorkReportStatisticRebuildJobFreshnessStates.Fresh, null);
    }

    private static NormalizedCreate NormalizeCreate(
        string capabilityId,
        StatRunCreateRequest request,
        NormalizedCommandScope authorizedScope)
    {
        request ??= new StatRunCreateRequest();
        capabilityId = RequiredText(capabilityId, "capabilityId", 80).ToUpperInvariant();
        var commandId = NormalizeCommandId(request.CommandId);
        var reportId = NormalizeObjectId(request.SourceReportId, "sourceReportId");
        var templateId = NormalizeObjectId(request.DynamicFormTemplateId, "dynamicFormTemplateId");
        if (request.ExpectedConfigRevision < 1)
            throw Validation("expectedConfigRevision", "REVISION_INVALID");
        if (request.ExpectedSourceRevision < 1)
            throw Validation("expectedSourceRevision", "REVISION_INVALID");
        if (request.ExpectedLifecycleRevision < 1)
            throw Validation("expectedLifecycleRevision", "REVISION_INVALID");
        RequireCanonicalHash(request.ExpectedConfigHash, "expectedConfigHash");
        RequireCanonicalHash(request.ExpectedSourceHash, "expectedSourceHash");

        var period = request.Period ?? new StatRunPeriodSelectorRequest();
        var periodKey = RequiredText(period.PeriodKey, "period.periodKey", 256);
        var periodInstanceKey = RequiredText(period.PeriodInstanceKey, "period.periodInstanceKey", 256);
        var periodKind = RequiredText(period.PeriodKind, "period.periodKind", 64);
        var periodStart = NormalizeUtc(period.PeriodStart);
        var periodEnd = NormalizeUtc(period.PeriodEnd);
        if (periodStart.HasValue && periodEnd.HasValue && periodEnd < periodStart)
            throw Validation("period", "TIME_RANGE_INVALID");

        return new NormalizedCreate(
            capabilityId,
            commandId,
            authorizedScope.WorkId,
            authorizedScope.ScopeType,
            authorizedScope.ScopeId,
            reportId,
            templateId,
            request.ExpectedConfigRevision,
            request.ExpectedConfigHash,
            request.ExpectedSourceRevision,
            request.ExpectedSourceHash,
            request.ExpectedLifecycleRevision,
            periodKey,
            periodInstanceKey,
            periodKind,
            periodStart,
            periodEnd);
    }

    private static NormalizedCommandScope NormalizeScopeForAuthorization(
        StatRunCreateRequest? request,
        MeResponse actor)
    {
        request ??= new StatRunCreateRequest();
        var workId = NormalizeCoarseObjectId(request.WorkId, "workId", actor);
        var scopeType = request.ScopeType?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(scopeType) ||
            scopeType.Length > 32 ||
            !ScopeTypes.Contains(scopeType))
        {
            throw CoarseTargetFailure(actor, "scopeType", "SCOPE_TYPE_INVALID");
        }

        string scopeId;
        if (scopeType == "WORK")
        {
            if (string.IsNullOrWhiteSpace(request.ScopeId))
            {
                scopeId = workId;
            }
            else
            {
                scopeId = NormalizeCoarseObjectId(request.ScopeId, "scopeId", actor);
                if (!string.Equals(scopeId, workId, StringComparison.Ordinal))
                    throw CoarseTargetFailure(actor, "scopeId", "WORK_SCOPE_ID_MISMATCH");
            }
        }
        else
        {
            scopeId = NormalizeCoarseObjectId(request.ScopeId, "scopeId", actor);
        }

        return new NormalizedCommandScope(workId, scopeType, scopeId);
    }

    private static string NormalizeCoarseObjectId(
        string? value,
        string field,
        MeResponse actor)
    {
        value = value?.Trim();
        if (!ObjectId.TryParse(value, out var parsed))
            throw CoarseTargetFailure(actor, field, "OBJECT_ID_INVALID");
        return parsed.ToString();
    }

    private static AppException CoarseTargetFailure(
        MeResponse actor,
        string field,
        string reason)
        => RoleGuard.IsSystemAdmin(actor)
            ? Validation(field, reason)
            : Forbidden();

    private static string NormalizeCommandId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.EnumerateRunes().Count() is < 1 or > 128 ||
            value.Any(char.IsControl))
        {
            throw new AppException(
                AppErrorCode.STAT_RUN_COMMAND_ID_REQUIRED,
                new { reason = "COMMAND_ID_INVALID", writes = 0 });
        }
        return value;
    }

    private static string NormalizeWorkerId(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '_' or ':' or '-')))
        {
            throw Validation("workerId", "WORKER_ID_INVALID");
        }
        return value;
    }

    private static string NormalizeClaimToken(string? value)
    {
        value = value?.Trim().ToLowerInvariant();
        if (value is null || value.Length != 32 ||
            value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw Validation("claimToken", "CLAIM_TOKEN_INVALID");
        }
        return value;
    }

    private static string NormalizeObjectId(string? value, string field)
    {
        value = value?.Trim();
        if (!ObjectId.TryParse(value, out var parsed))
            throw Validation(field, "OBJECT_ID_INVALID");
        return parsed.ToString();
    }

    private static string RequiredText(string? value, string field, int maxLength)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw Validation(field, "VALUE_REQUIRED");
        if (value.Length > maxLength)
            throw Validation(field, "VALUE_TOO_LONG");
        return value;
    }

    private static void RequireCanonicalHash(string? value, string field)
    {
        if (!StatRunCanonicalJson.IsCanonicalSha256(value))
            throw Validation(field, "SHA256_CANONICAL_INVALID");
    }

    private DateTime MongoUtcNow()
        => NormalizeUtc(_time.UtcNow)!.Value;

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue)
            return null;
        var utc = value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
        return new DateTime(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
    }

    private static string NormalizeTenantKey(string? unitId)
        => ObjectId.TryParse(unitId, out var parsed) ? parsed.ToString() : "NO_UNIT";

    private static void RequireCommandRole(MeResponse actor)
    {
        if (RoleGuard.IsSystemAdmin(actor) ||
            RoleGuard.IsAdmin(actor) ||
            RoleGuard.IsManagerLevel(actor))
        {
            return;
        }

        if (RoleGuard.TryGetManagerUnit(actor, out var managedUnitId) &&
            string.Equals(managedUnitId, actor.UnitId, StringComparison.Ordinal))
        {
            return;
        }

        throw Forbidden();
    }

    private static string BuildJobDedupeKey(
        NormalizedCreate request,
        MeResponse actor,
        StatRunCandidateBinding binding,
        WorkAssignmentReport report,
        WorkAssignment assignment,
        DynamicFormTemplate template,
        DynamicFlowTemplate? flowFamily,
        DynamicFlowTemplateVersion? flowVersion,
        DynamicFlowInstance? flowInstance,
        DynamicFlowExecutionEpoch? flowEpoch,
        DynamicFlowStepInstance? flowStep)
        => $"stat-run:{StatRunCanonicalJson.HashObject(new
        {
            request.CapabilityId,
            tenantUnitId = NormalizeTenantKey(actor.UnitId),
            request.WorkId,
            request.ScopeType,
            request.ScopeId,
            sourceReportId = report.Id,
            sourcePayloadRevision = report.PayloadRevision,
            sourcePayloadHash = report.PayloadHash,
            sourceLifecycleRevision = report.LifecycleRevision,
            configId = template.StatisticConfigId,
            configVersionId = template.StatisticConfigVersionId,
            configVersionNo = template.StatisticConfigVersionNo,
            configRevision = template.StatisticConfigRevision,
            configHash = template.StatisticConfigHash,
            binding.CatalogVersion,
            binding.CatalogRawSha256,
            binding.CatalogSemanticSha256,
            binding.SchemaRawSha256,
            binding.SchemaSemanticSha256,
            binding.StageLockSha256,
            binding.ChainId,
            assignment.FlowTemplateId,
            flowFamilyRevision = flowFamily?.FamilyRevision,
            flowTemplateVersionId = flowVersion?.Id,
            flowPayloadHash = flowVersion?.PayloadHash,
            flowCatalogVersion = flowVersion?.CatalogVersion,
            flowCatalogSemanticHash = flowVersion?.CatalogSemanticHash,
            assignment.FlowInstanceId,
            flowInstanceRevision = flowInstance?.Revision,
            flowInstanceState = flowInstance?.State,
            assignment.FlowExecutionEpoch,
            flowExecutionEpochId = flowEpoch?.Id,
            flowExecutionEpochRevision = flowEpoch?.Revision,
            flowExecutionEpochState = flowEpoch?.State,
            assignment.FlowBranchId,
            assignment.FlowStepId,
            assignment.FlowAttemptNo,
            flowStepInstanceId = flowStep?.Id,
            flowStepInstanceRevision = flowStep?.Revision,
            flowStepInstanceState = flowStep?.State,
            report.PeriodKey,
            report.PeriodInstanceKey,
            report.PeriodKind,
            periodStartUtc = FormatUtc(report.PeriodStart),
            periodEndUtc = FormatUtc(report.PeriodEnd)
        })}";

    private static string BuildImmutableHeaderHash(WorkReportStatisticRebuildJob job)
        => StatRunCanonicalJson.HashObject(new
        {
            schema = "STAT_RUN_IMMUTABLE_HEADER_V1",
            job.Id,
            job.DedupeKey,
            job.ReceiptId,
            job.CommandId,
            job.RequestHash,
            job.ReceiptAcceptedAtUtc,
            job.CapabilityId,
            job.RouteId,
            job.RunKind,
            job.ActorUserId,
            job.TenantUnitId,
            job.ScopeType,
            job.ScopeId,
            job.SourceReportId,
            job.SourcePayloadRevision,
            job.SourcePayloadHash,
            job.SourceLifecycleRevision,
            job.SourceStatus,
            job.ConfigId,
            job.ConfigVersionId,
            job.ConfigVersionNo,
            job.ConfigRevision,
            job.ConfigHash,
            job.CatalogVersion,
            job.CatalogRawSha256,
            job.CatalogSemanticSha256,
            job.SchemaRawSha256,
            job.SchemaSemanticSha256,
            job.StageLockSha256,
            job.CandidateChainId,
            job.DynamicFormTemplateId,
            job.DynamicFormTemplateCode,
            job.DynamicFormTemplateName,
            job.ScopeKind,
            job.WorkId,
            job.WorkAssignmentId,
            job.FlowInstanceId,
            job.FlowInstanceRevision,
            job.FlowInstanceState,
            job.FlowEffectiveStatus,
            job.FlowTemplateId,
            job.FlowFamilyRevision,
            job.FlowTemplateVersionNo,
            job.FlowTemplateVersionId,
            job.FlowPayloadHash,
            job.FlowCatalogVersion,
            job.FlowCatalogSemanticHash,
            job.FlowExecutionEpoch,
            job.FlowExecutionEpochId,
            job.FlowExecutionEpochRevision,
            job.FlowExecutionEpochState,
            job.FlowBranchId,
            job.FlowStepId,
            job.FlowAttemptNo,
            job.FlowStepInstanceId,
            job.FlowStepInstanceRevision,
            job.FlowStepInstanceState,
            job.PeriodKey,
            job.PeriodInstanceKey,
            job.PeriodKind,
            periodStartUtc = FormatUtc(job.PeriodStartUtc),
            periodEndUtc = FormatUtc(job.PeriodEndUtc),
            job.RequestedByUserId,
            createdAtUtc = FormatUtc(job.CreatedAtUtc),
            job.CreatedByUserId,
            initialDeadlineAtUtc = FormatUtc(job.InitialDeadlineAtUtc)
        });

    private static string BuildResetReceiptHash(
        WorkReportStatisticRebuildJobResetReceipt receipt)
        => StatRunCanonicalJson.HashObject(new
        {
            schema = "STAT_RUN_RESET_RECEIPT_V1",
            receipt.JobId,
            receipt.ActorUserId,
            receipt.CommandId,
            receipt.RequestHash,
            receipt.ExpectedStateRevision,
            receipt.ExpectedStateHash,
            receipt.AcceptedStateRevision,
            receipt.AcceptedStatus,
            acceptedAtUtc = FormatUtc(receipt.AcceptedAtUtc),
            acceptedDeadlineAtUtc = FormatUtc(receipt.AcceptedDeadlineAtUtc)
        });

    private static string BuildResetReceiptHistoryHash(
        IEnumerable<WorkReportStatisticRebuildJobResetReceipt> receipts)
        => StatRunCanonicalJson.HashObject(new
        {
            schema = "STAT_RUN_RESET_RECEIPT_HISTORY_V1",
            receipts = receipts.Select(receipt => new
            {
                receipt.ReceiptHash,
                computedReceiptHash = BuildResetReceiptHash(receipt)
            }).ToArray()
        });

    private static string BuildAcceptedResponseHash(WorkReportStatisticRebuildJob job)
        => StatRunCanonicalJson.HashObject(new
        {
            schema = "STAT_RUN_ACCEPTED_RESPONSE_V1",
            job.Id,
            job.ReceiptId,
            job.CommandId,
            job.RequestHash,
            job.ImmutableHeaderHash,
            job.ReceiptAcceptedAtUtc
        });

    private static void RequireImmutableHeaderIntegrity(
        WorkReportStatisticRebuildJob job)
    {
        if (!StatRunCanonicalJson.IsCanonicalSha256(job.ImmutableHeaderHash) ||
            !string.Equals(
                job.ImmutableHeaderHash,
                BuildImmutableHeaderHash(job),
                StringComparison.Ordinal))
        {
            throw JobConflict("IMMUTABLE_HEADER_INTEGRITY_INVALID");
        }
        if (!StatRunCanonicalJson.IsCanonicalSha256(job.ReceiptResponseHash) ||
            !string.Equals(
                job.ReceiptResponseHash,
                BuildAcceptedResponseHash(job),
                StringComparison.Ordinal))
        {
            throw JobConflict("RECEIPT_INTEGRITY_INVALID");
        }
    }

    private static string BuildStateHash(
        string jobId,
        string status,
        long revision,
        int retryCount,
        DateTime? nextRetryAtUtc,
        DateTime? leaseUntilUtc,
        DateTime? deadlineAtUtc,
        string? claimToken,
        string? leaseOwnerId,
        DateTime? lastHeartbeatAtUtc,
        string? generationId,
        string? generationHash,
        string? resetReceiptHistoryHash,
        string freshnessState,
        string? diagnosticCode)
        => StatRunCanonicalJson.HashObject(new
        {
            jobId,
            status,
            revision,
            retryCount,
            nextRetryAtUtc = FormatUtc(nextRetryAtUtc),
            leaseUntilUtc = FormatUtc(leaseUntilUtc),
            deadlineAtUtc = FormatUtc(deadlineAtUtc),
            claimToken,
            leaseOwnerId,
            lastHeartbeatAtUtc = FormatUtc(lastHeartbeatAtUtc),
            generationId,
            generationHash,
            resetReceiptHistoryHash,
            freshnessState,
            diagnosticCode
        });

    private static void RequireStateIntegrity(WorkReportStatisticRebuildJob job)
    {
        RequireResetReceiptHistoryIntegrity(job);
        var expected = BuildStateHash(
            job.Id,
            job.Status,
            job.StateRevision,
            job.RetryCount,
            job.NextRetryAtUtc,
            job.LeaseUntilUtc,
            job.DeadlineAtUtc,
            job.ClaimToken,
            job.LeaseOwnerId,
            job.LastHeartbeatAtUtc,
            job.GenerationId,
            job.GenerationHash,
            job.ResetReceiptHistoryHash,
            job.FreshnessState ?? WorkReportStatisticRebuildJobFreshnessStates.Pending,
            job.DiagnosticCode);
        if (!string.Equals(job.StateHash, expected, StringComparison.Ordinal))
            throw JobConflict("STATE_INTEGRITY_INVALID");
    }

    private static void RequireResetReceiptHistoryIntegrity(
        WorkReportStatisticRebuildJob job)
    {
        var receipts = job.ResetReceipts ?? [];
        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < receipts.Count; index++)
        {
            var receipt = receipts[index];
            var identity = $"{receipt.ActorUserId}\n{receipt.CommandId}";
            var prefixHistoryHash = BuildResetReceiptHistoryHash(
                receipts.Take(index + 1));
            var expectedAcceptedStateHash = BuildStateHash(
                receipt.JobId,
                WorkReportStatisticRebuildJobStatuses.Pending,
                receipt.AcceptedStateRevision,
                0,
                receipt.AcceptedAtUtc,
                null,
                receipt.AcceptedDeadlineAtUtc,
                null,
                null,
                null,
                null,
                null,
                prefixHistoryHash,
                WorkReportStatisticRebuildJobFreshnessStates.Pending,
                null);
            if (!identities.Add(identity) ||
                !string.Equals(receipt.JobId, job.Id, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(receipt.ActorUserId) ||
                string.IsNullOrWhiteSpace(receipt.CommandId) ||
                !StatRunCanonicalJson.IsCanonicalSha256(receipt.RequestHash) ||
                receipt.ExpectedStateRevision < 1 ||
                !StatRunCanonicalJson.IsCanonicalSha256(receipt.ExpectedStateHash) ||
                receipt.AcceptedStateRevision != receipt.ExpectedStateRevision + 1 ||
                !string.Equals(
                    receipt.AcceptedStatus,
                    WorkReportStatisticRebuildJobStatuses.Pending,
                    StringComparison.Ordinal) ||
                NormalizeUtc(receipt.AcceptedDeadlineAtUtc) !=
                NormalizeUtc(receipt.AcceptedAtUtc.AddMinutes(10)) ||
                !StatRunCanonicalJson.IsCanonicalSha256(receipt.ReceiptHash) ||
                !string.Equals(
                    receipt.ReceiptHash,
                    BuildResetReceiptHash(receipt),
                    StringComparison.Ordinal) ||
                !StatRunCanonicalJson.IsCanonicalSha256(receipt.AcceptedStateHash) ||
                !string.Equals(
                    receipt.AcceptedStateHash,
                    expectedAcceptedStateHash,
                    StringComparison.Ordinal))
            {
                throw JobConflict("RESET_RECEIPT_HISTORY_INTEGRITY_INVALID");
            }
        }

        var resetHistoryHash = BuildResetReceiptHistoryHash(receipts);
        if (!StatRunCanonicalJson.IsCanonicalSha256(job.ResetReceiptHistoryHash) ||
            !string.Equals(
                job.ResetReceiptHistoryHash,
                resetHistoryHash,
                StringComparison.Ordinal))
        {
            throw JobConflict("RESET_RECEIPT_HISTORY_INTEGRITY_INVALID");
        }
    }

    private static string? FormatUtc(DateTime? value)
        => NormalizeUtc(value)?.ToString("O", CultureInfo.InvariantCulture);

    private static string ToApiStatus(string status)
        => status switch
        {
            WorkReportStatisticRebuildJobStatuses.Pending => "QUEUED",
            WorkReportStatisticRebuildJobStatuses.Running => "RUNNING",
            WorkReportStatisticRebuildJobStatuses.RetryWaiting => "RETRYING",
            WorkReportStatisticRebuildJobStatuses.Completed => "DONE",
            WorkReportStatisticRebuildJobStatuses.DeadLetter => "FAILED",
            _ => "FAILED"
        };

    private static string? RedactDiagnostic(string? value)
        => value is "TRANSIENT_TEST" or "TIMEOUT_TEST" or "TERMINAL_TEST" or "STAT_RUN_JOB_TIMEOUT"
            ? value
            : string.IsNullOrWhiteSpace(value) ? null : "STAT_RUN_JOB_FAILED";

    private static AppException Validation(string field, string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new { field, reason, writes = 0 });

    private static AppException Forbidden()
        => new(AppErrorCode.STAT_RUN_FORBIDDEN, new { writes = 0 });

    private static AppException SourceConflict(string reason)
        => new(AppErrorCode.STAT_RUN_SOURCE_NOT_EFFECTIVE, new { reason, writes = 0 });

    private static AppException HiddenSourceFailure(MeResponse actor, string reason)
        => RoleGuard.IsSystemAdmin(actor)
            ? SourceConflict(reason)
            : Forbidden();

    private static AppException ConfigConflict(string reason)
        => new(AppErrorCode.STAT_RUN_CONFIG_STALE, new { reason, writes = 0 });

    private static AppException JobConflict(string reason, int writes = 0)
        => new(AppErrorCode.STAT_RUN_JOB_CONFLICT, new StatRunJobConflictDetails(reason, writes));

    private sealed record NormalizedCreate(
        string CapabilityId,
        string CommandId,
        string WorkId,
        string ScopeType,
        string ScopeId,
        string SourceReportId,
        string DynamicFormTemplateId,
        long ExpectedConfigRevision,
        string ExpectedConfigHash,
        int ExpectedSourceRevision,
        string ExpectedSourceHash,
        int ExpectedLifecycleRevision,
        string PeriodKey,
        string PeriodInstanceKey,
        string PeriodKind,
        DateTime? PeriodStartUtc,
        DateTime? PeriodEndUtc);

    private sealed record NormalizedCommandScope(
        string WorkId,
        string ScopeType,
        string ScopeId);
}
