using System.Globalization;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService
{
    private const int MaxOperationReceipts = 128;
    private const string SummaryPermission = "RECONCILIATION_SUMMARY";
    private const string DetailPermission = "RECONCILIATION_DETAIL";

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

    private static void RequireDetailRole(MeResponse actor)
    {
        if (RoleGuard.IsSystemAdmin(actor) || RoleGuard.IsAdmin(actor))
            return;
        throw Forbidden();
    }

    private async Task<WorkAssignment> AuthorizeScopeAsync(
        string workId,
        string scopeAssignmentId,
        MeResponse actor,
        CancellationToken ct)
    {
        RequireCommandRole(actor);
        workId = NormalizeScopedObjectId(workId, "workId", actor);
        scopeAssignmentId = NormalizeScopedObjectId(
            scopeAssignmentId,
            "scopeAssignmentId",
            actor);

        var fb = Builders<WorkAssignment>.Filter;
        var filter = fb.Eq(x => x.Id, scopeAssignmentId) &
                     fb.Eq(x => x.WorkId, workId) &
                     fb.Eq(x => x.IsDeleted, false) &
                     fb.Eq(x => x.IsActive, true);
        if (!RoleGuard.IsSystemAdmin(actor))
        {
            if (!ObjectId.TryParse(actor.UnitId, out var unitId))
                throw Forbidden();
            filter &= (fb.Eq(x => x.CreatedByUserId, actor.Id) |
                       fb.AnyEq(x => x.LeaderWatcherUserIds, actor.Id)) &
                      (fb.Eq(x => x.IssuedByUnitId, unitId.ToString()) |
                       fb.AnyEq(x => x.TargetUnitIds!, unitId.ToString()));
        }

        var assignment = await _ctx.WorkAssignments.Find(filter).FirstOrDefaultAsync(ct);
        if (assignment is not null)
            return assignment;
        if (RoleGuard.IsSystemAdmin(actor))
            throw NotFound();
        throw Forbidden();
    }

    internal static bool HasInvalidP9FlowRuntime(
        WorkReportStatisticRebuildJob job)
    {
        if (job.FlowInstanceId is null)
        {
            return job.FlowTemplateId is not null ||
                   job.FlowFamilyRevision is not null ||
                   job.FlowTemplateVersionNo is not null ||
                   job.FlowTemplateVersionId is not null ||
                   job.FlowContributionOriginVersionId is not null ||
                   job.FlowPayloadHash is not null ||
                   job.FlowCatalogVersion is not null ||
                   job.FlowCatalogSemanticHash is not null ||
                   job.FlowInstanceRevision is not null ||
                   job.FlowInstanceState is not null ||
                   job.FlowExecutionEpoch is not null ||
                   job.FlowExecutionEpochId is not null ||
                   job.FlowExecutionEpochRevision is not null ||
                   job.FlowExecutionEpochState is not null ||
                   job.FlowStepId is not null ||
                   job.FlowBranchId is not null ||
                   job.FlowAttemptNo is not null ||
                   job.FlowStepInstanceId is not null ||
                   job.FlowStepInstanceRevision is not null ||
                   job.FlowStepInstanceState is not null ||
                   job.FlowContributionPolicy is not null ||
                   job.FlowContributionPolicyHash is not null ||
                   job.FlowContributionWarning is not null ||
                   job.FlowEffectiveStatus is not null;
        }

        return !ObjectId.TryParse(job.FlowTemplateId, out _) ||
               job.FlowFamilyRevision is not > 0 ||
               !ObjectId.TryParse(job.FlowTemplateVersionId, out _) ||
               !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                   job.FlowPayloadHash) ||
               job.FlowInstanceRevision is not > 0 ||
               job.FlowExecutionEpoch is not > 0 ||
               !ObjectId.TryParse(job.FlowExecutionEpochId, out _) ||
               job.FlowExecutionEpochRevision is not > 0 ||
               string.IsNullOrWhiteSpace(job.FlowStepId) ||
               !ObjectId.TryParse(job.FlowBranchId, out _) ||
               !ObjectId.TryParse(job.FlowStepInstanceId, out _) ||
               job.FlowStepInstanceRevision is not > 0 ||
               string.IsNullOrWhiteSpace(job.FlowContributionPolicy) ||
               !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                   job.FlowContributionPolicyHash) ||
               string.IsNullOrWhiteSpace(job.FlowEffectiveStatus);
    }

    private Task<WorkReportStatisticRebuildJob> LoadP9PublicationAsync(
        NormalizedCreate request,
        WorkAssignment scope,
        MeResponse actor,
        CancellationToken ct)
        => LoadP9PublicationCoreAsync(
            request,
            scope,
            serverRecheckFamily: null,
            actor,
            ct);

    private Task<WorkReportStatisticRebuildJob>
        LoadP9PublicationForRecheckAsync(
            NormalizedCreate request,
            WorkAssignment scope,
            StatisticReconciliationRun serverRecheckFamily,
            MeResponse actor,
            CancellationToken ct)
        => LoadP9PublicationCoreAsync(
            request,
            scope,
            serverRecheckFamily,
            actor,
            ct);

    private async Task<WorkReportStatisticRebuildJob>
        LoadP9PublicationCoreAsync(
            NormalizedCreate request,
            WorkAssignment scope,
            StatisticReconciliationRun? serverRecheckFamily,
            MeResponse actor,
            CancellationToken ct)
    {
        var p9RunId = NormalizeHiddenObjectId(request.P9RunId, "p9RunId", actor);
        var p9ResultId = NormalizeHiddenObjectId(request.P9ResultId, "p9ResultId", actor);
        if (!string.Equals(p9RunId, p9ResultId, StringComparison.Ordinal) ||
            !string.Equals(
                request.P9ResultKind,
                StatisticReconciliationP9ResultKinds.Direct,
                StringComparison.Ordinal))
        {
            throw HiddenSourceFailure(actor, "P9_RESULT_BINDING_INVALID");
        }

        var job = await _ctx.WorkReportStatisticRebuildJobs
            .Find(BuildP9PublicationLookupFilter(
                Builders<WorkReportStatisticRebuildJob>.Filter,
                p9RunId,
                scope.WorkId,
                scope.Id,
                serverRecheckFamily))
            .FirstOrDefaultAsync(ct);
        if (job is null)
            throw HiddenSourceFailure(actor, "P9_RESULT_NOT_EFFECTIVE");

        var expectedP9StateHash = StatisticReconciliationCanonicalJson.HashObject(new
        {
            version = "P9_LIFECYCLE_DIRECT_STATE_V1",
            runId = job.Id,
            status = job.Status,
            revision = job.StateRevision,
            claimToken = (string?)null,
            workerId = (string?)null,
            generationId = job.GenerationId,
            generationHash = job.GenerationHash
        });
        var stores = (job.DirectStoreDigests ?? [])
            .OrderBy(item => item.Store, StringComparer.Ordinal)
            .ToArray();
        var expectedStores = new[]
        {
            "work_report_field_stat_aggregates",
            "work_report_field_stat_values",
            "work_report_label_stat_aggregates",
            "work_report_label_stat_values",
            "work_report_table_stat_aggregates",
            "work_report_table_stat_values"
        };
        var expectedPublicationScopeKey =
            StatisticReconciliationCanonicalJson.HashObject(new
            {
                version = "P9_DIRECT_PUBLICATION_SCOPE_V1",
                job.WorkId,
                job.PeriodInstanceKey,
                dynamicFormFamilyId = job.DynamicFormFamilyId,
                dynamicFormTemplateId = job.DynamicFormTemplateId,
                dynamicFormVersionNo = job.DynamicFormVersionNo,
                dynamicFormSchemaHash = job.DynamicFormSchemaHash,
                configVersionId = job.ConfigVersionId,
                configRevision = job.ConfigRevision,
                configHash = job.ConfigHash
            });
        if (!ObjectId.TryParse(job.SourceReportId, out _) ||
            job.SourcePayloadRevision is not > 0 ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(job.SourcePayloadHash) ||
            job.SourceLifecycleRevision is not > 0 ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(job.SourceLifecycleEventKey) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(job.SourceMembershipSignature) ||
            job.DirectSourceRevision is not > 0 ||
            !string.Equals(
                job.PublicationScopeKey,
                expectedPublicationScopeKey,
                StringComparison.Ordinal) ||
            job.DirectPublicationRevision is not > 0 ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(job.GenerationId) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(job.GenerationHash) ||
            !string.Equals(job.StateHash, expectedP9StateHash, StringComparison.Ordinal) ||
            !string.Equals(job.ScopeType, "WORK_PERIOD_TEMPLATE", StringComparison.Ordinal) ||
            !string.Equals(job.ScopeId, scope.WorkId, StringComparison.Ordinal) ||
            !string.Equals(
                job.ScopeKind,
                WorkReportStatisticRebuildJobScopeKinds.Bounded,
                StringComparison.Ordinal) ||
            !string.Equals(
                job.FreshnessState,
                WorkReportStatisticRebuildJobFreshnessStates.Fresh,
                StringComparison.Ordinal) ||
            !job.PublishedAtUtc.HasValue ||
            !job.CompletedAtUtc.HasValue ||
            !job.ComputedAtUtc.HasValue ||
            string.IsNullOrWhiteSpace(job.SourceStatus) ||
            !ObjectId.TryParse(job.DynamicFormFamilyId, out _) ||
            !ObjectId.TryParse(job.DynamicFormTemplateId, out _) ||
            job.DynamicFormVersionNo is not > 0 ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                job.DynamicFormSchemaHash) ||
            HasInvalidP9FlowRuntime(job) ||
            string.IsNullOrWhiteSpace(job.FlowContributionOperationVersion) ||
            (!string.IsNullOrWhiteSpace(job.FlowContributionLedgerHash) &&
             !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                 job.FlowContributionLedgerHash)) ||
            !string.Equals(
                job.CatalogVersion,
                StatRunCapabilityActivation.RequiredCatalogVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                job.CatalogRawSha256,
                StatRunCapabilityActivation.PublishedCatalogRawSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                job.CatalogSemanticSha256,
                StatRunCapabilityActivation.PublishedCatalogSemanticSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                job.SchemaRawSha256,
                StatRunCapabilityActivation.PublishedSchemaRawSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                job.SchemaSemanticSha256,
                StatRunCapabilityActivation.PublishedSchemaSemanticSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                job.StageLockSha256,
                StatRunCapabilityActivation.PublishedSealStageLockRawSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                job.CapabilityId,
                StatRunCapabilities.DirectFieldTableLabel,
                StringComparison.Ordinal) ||
            !string.Equals(
                job.RouteId,
                StatRunRouteRegistry.LifecycleDirectProjector,
                StringComparison.Ordinal) ||
            !string.Equals(
                job.CandidateChainId,
                StatRunCapabilityActivation.RequiredChainId,
                StringComparison.Ordinal) ||
            !string.Equals(
                job.CandidatePromptId,
                StatRunCapabilityActivation.PublishedPromptId,
                StringComparison.Ordinal) ||
            stores.Length != expectedStores.Length ||
            !stores.Select(item => item.Store).SequenceEqual(expectedStores, StringComparer.Ordinal) ||
            stores.Any(item => item.RowCount < 0 ||
                               !StatisticReconciliationCanonicalJson.IsCanonicalSha256(item.Sha256)))
        {
            throw HiddenSourceFailure(actor, "P9_RESULT_INTEGRITY_INVALID");
        }

        if (!ObjectId.TryParse(job.ConfigId, out _) ||
            !ObjectId.TryParse(job.ConfigVersionId, out _) ||
            job.ConfigVersionNo is not > 0 ||
            job.ConfigRevision is not > 0 ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(job.ConfigHash) ||
            string.IsNullOrWhiteSpace(job.PeriodKey) ||
            string.IsNullOrWhiteSpace(job.PeriodInstanceKey) ||
            string.IsNullOrWhiteSpace(job.PeriodKind) ||
            job.PeriodStartUtc.HasValue != job.PeriodEndUtc.HasValue ||
            (job.PeriodStartUtc.HasValue &&
             job.PeriodStartUtc.Value > job.PeriodEndUtc!.Value))
        {
            throw HiddenSourceFailure(actor, "P9_CONFIG_OR_PERIOD_PIN_INVALID");
        }
        return job;
    }

    internal static FilterDefinition<WorkReportStatisticRebuildJob>
        BuildP9PublicationLookupFilter(
            FilterDefinitionBuilder<WorkReportStatisticRebuildJob> filter,
            string p9RunId,
            string workId,
            string scopeAssignmentId,
            StatisticReconciliationRun? serverRecheckFamily)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (serverRecheckFamily is not null)
        {
            if (!string.Equals(
                    serverRecheckFamily.WorkId,
                    workId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    serverRecheckFamily.ScopeAssignmentId,
                    scopeAssignmentId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "P9_RECHECK_SCOPE_BINDING_INVALID");
            }

            return filter.Eq(job => job.Id, p9RunId) &
                   BuildP10CurrentP9PublicationFamilyFilter(
                       filter,
                       serverRecheckFamily);
        }

        return filter.Eq(job => job.Id, p9RunId) &
               filter.Eq(job => job.WorkId, workId) &
               filter.Eq(
                   job => job.WorkAssignmentId,
                   scopeAssignmentId) &
               filter.Eq(
                   job => job.RunKind,
                   WorkReportStatisticRebuildJobRunKinds
                       .LifecycleDirectProjection) &
               filter.Eq(
                   job => job.Status,
                   WorkReportStatisticRebuildJobStatuses.Completed) &
               filter.Eq(job => job.IsCurrentPublication, true) &
               filter.Eq(job => job.IsActive, false) &
               filter.Eq(job => job.IsDeleted, false);
    }

    private async Task<StatisticReconciliationRun> LoadAuthorizedRunAsync(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        string requiredChainId,
        MeResponse actor,
        CancellationToken ct)
    {
        reconciliationId = NormalizeHiddenObjectId(
            reconciliationId,
            "reconciliationId",
            actor);
        var run = await _ctx.StatisticReconciliationRuns
            .Find(x => x.Id == reconciliationId &&
                       x.WorkId == workId &&
                       x.ScopeAssignmentId == scopeAssignmentId &&
                       x.CandidateChainId == requiredChainId &&
                       !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (run is not null)
            return run;
        if (RoleGuard.IsSystemAdmin(actor))
            throw NotFound();
        throw Forbidden();
    }

    private static NormalizedCreate NormalizeCreate(
        StatisticReconciliationCreateRequest request,
        MeResponse actor)
    {
        if (request is null)
            throw RequestInvalid("body", "BODY_REQUIRED");
        var commandId = NormalizeCommandId(request.CommandId);
        var p9ResultKind = RequiredToken(
            request.P9ResultKind,
            "p9ResultKind",
            64).ToUpperInvariant();
        var p9ResultId = RequiredText(request.P9ResultId, "p9ResultId", 64);
        var p9RunId = RequiredText(request.P9RunId, "p9RunId", 64);
        var conceptKey = RequiredToken(request.ConceptKey, "conceptKey", 160);
        var grain = RequiredToken(request.Grain, "grain", 64).ToUpperInvariant();
        var capturePlanToken = NormalizeCapturePlanToken(request.CapturePlanToken);
        if (request.ActualCapturePlan is not null && capturePlanToken is not null)
            throw RequestInvalid(
                "capturePlanToken",
                "TOKEN_AND_ACTUAL_CAPTURE_PLAN_MUTUALLY_EXCLUSIVE");
        string? canonicalFilterJson = null;
        string filterHash;
        if (request.ActualCapturePlan is null && capturePlanToken is null)
        {
            if (request.Filter is { } legacyFilter &&
                legacyFilter.ValueKind is not (
                    JsonValueKind.Object or JsonValueKind.Null))
            {
                throw RequestInvalid("filter", "OBJECT_OR_NULL_REQUIRED");
            }
            filterHash = StatisticReconciliationCanonicalJson.HashElement(
                request.Filter);
        }
        else
        {
            if (request.Filter is not { } planFilter ||
                planFilter.ValueKind != JsonValueKind.Object)
            {
                throw RequestInvalid("filter", "OBJECT_REQUIRED_WITH_ACTUAL_CAPTURE_PLAN");
            }
            canonicalFilterJson =
                StatisticReconciliationCanonicalJson.Canonicalize(planFilter);
            filterHash = StatisticReconciliationCanonicalJson.HashText(
                canonicalFilterJson);
        }
        var actualCapturePlan = NormalizeActualCapturePlanRequest(
            request.ActualCapturePlan,
            actor);
        return new NormalizedCreate(
            commandId,
            p9ResultKind,
            p9ResultId,
            p9RunId,
            conceptKey,
            grain,
            filterHash,
            canonicalFilterJson,
            actualCapturePlan,
            capturePlanToken);
    }

    private static NormalizedActualCapturePlanRequest?
        NormalizeActualCapturePlanRequest(
            StatisticReconciliationActualCapturePlanRequest? request,
            MeResponse actor)
    {
        if (request is null)
            return null;
        var registryVersion = RequiredToken(
            request.BoundaryRegistryVersion,
            "actualCapturePlan.boundaryRegistryVersion",
            96);
        if (!string.Equals(
                registryVersion,
                StatisticReconciliationActualCapturePlanIntegrity
                    .BoundaryRegistryVersion,
                StringComparison.Ordinal))
        {
            throw RequestInvalid(
                "actualCapturePlan.boundaryRegistryVersion",
                "BOUNDARY_REGISTRY_VERSION_UNSUPPORTED");
        }
        var basicSnapshotId = NormalizeHiddenObjectId(
            request.BasicSnapshotId,
            "actualCapturePlan.basicSnapshotId",
            actor);
        var basicMode = RequiredToken(
            request.BasicMode,
            "actualCapturePlan.basicMode",
            64).ToUpperInvariant();
        if (basicMode is not (
                "DIRECT_CHILDREN_OR_SELF" or "DIRECT_CHILDREN" or
                "FLOW_BRANCH" or "FLOW_STEP" or
                "FLOW_EFFECTIVE_PATH" or "FLOW_FINAL"))
        {
            throw RequestInvalid(
                "actualCapturePlan.basicMode",
                "BASIC_MODE_UNSUPPORTED");
        }
        var sectionId = RequiredToken(
            request.AdvancedSectionId,
            "actualCapturePlan.advancedSectionId",
            160);
        var dayIds = NormalizePlanObjectIds(
            request.AdvancedDayNodeIds,
            "actualCapturePlan.advancedDayNodeIds",
            actor);
        var monthIds = NormalizePlanObjectIds(
            request.AdvancedMonthNodeIds,
            "actualCapturePlan.advancedMonthNodeIds",
            actor);
        var yearIds = NormalizePlanObjectIds(
            request.AdvancedYearNodeIds,
            "actualCapturePlan.advancedYearNodeIds",
            actor);
        if (checked(dayIds.Count + monthIds.Count + yearIds.Count) > 4096)
        {
            throw RequestInvalid(
                "actualCapturePlan.advancedNodeIds",
                "EXACT_ID_LIMIT_EXCEEDED");
        }
        var diffResultId = NormalizeHiddenObjectId(
            request.DiffResultId,
            "actualCapturePlan.diffResultId",
            actor);
        var diffRunId = NormalizeHiddenObjectId(
            request.DiffRunId,
            "actualCapturePlan.diffRunId",
            actor);
        var apiSurface = RequiredToken(
            request.ApiSurface,
            "actualCapturePlan.apiSurface",
            64).ToUpperInvariant();
        if (apiSurface is not (
                "DIRECT_FIELD" or "DIRECT_TABLE" or "DIRECT_LABEL" or
                "BASIC_SOURCE" or "P9_DIFF"))
        {
            throw RequestInvalid(
                "actualCapturePlan.apiSurface",
                "API_SURFACE_UNSUPPORTED");
        }
        var apiOwnerResultId = NormalizeHiddenObjectId(
            request.ApiOwnerResultId,
            "actualCapturePlan.apiOwnerResultId",
            actor);
        var exportId = RequiredText(
            request.ExportId,
            "actualCapturePlan.exportId",
            128);
        if (!ValidActualExportId(exportId))
            throw HiddenSourceFailure(actor, "ACTUAL_EXPORT_ID_INVALID");
        return new NormalizedActualCapturePlanRequest(
            registryVersion,
            basicSnapshotId,
            basicMode,
            sectionId,
            dayIds,
            monthIds,
            yearIds,
            diffResultId,
            diffRunId,
            apiSurface,
            apiOwnerResultId,
            exportId);
    }

    internal static bool ValidActualExportId(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(value))
            return !string.IsNullOrEmpty(value);
        return ObjectId.TryParse(value, out var parsed) &&
            StringComparer.Ordinal.Equals(value, parsed.ToString());
    }
    private static IReadOnlyList<string> NormalizePlanObjectIds(
        IReadOnlyList<string>? values,
        string field,
        MeResponse actor)
    {
        if (values is null || values.Count is < 1 or > 4096)
            throw RequestInvalid(field, "NON_EMPTY_EXACT_ID_SET_REQUIRED");
        var result = values
            .Select(value => NormalizeHiddenObjectId(value, field, actor))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw RequestInvalid(field, "DUPLICATE_ID");
        return result;
    }


    private static string? NormalizeCapturePlanToken(string? value)
    {
        if (value is null)
            return null;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 32 * 1024 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw RequestInvalid("capturePlanToken", "TOKEN_FORMAT_INVALID");
        }
        return value;
    }

    private static string NormalizeCommandId(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new AppException(
                AppErrorCode.STAT_RECONCILIATION_COMMAND_ID_REQUIRED,
                new { writes = 0 });
        if (value.Length > 128 || value.Any(char.IsControl))
            throw RequestInvalid("commandId", "COMMAND_ID_INVALID");
        return value;
    }

    private static string RequiredText(string? value, string field, int maxLength)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw RequestInvalid(field, "VALUE_REQUIRED");
        if (value.Length > maxLength || value.Any(char.IsControl))
            throw RequestInvalid(field, "VALUE_INVALID");
        return value;
    }

    private static string RequiredToken(string? value, string field, int maxLength)
    {
        value = RequiredText(value, field, maxLength);
        if (value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '_' or '-' or '.' or ':' or '/')))
        {
            throw RequestInvalid(field, "TOKEN_INVALID");
        }
        return value;
    }

    private static string NormalizeScopedObjectId(
        string? value,
        string field,
        MeResponse actor)
    {
        value = value?.Trim();
        if (ObjectId.TryParse(value, out var parsed))
            return parsed.ToString();
        if (RoleGuard.IsSystemAdmin(actor))
            throw RequestInvalid(field, "OBJECT_ID_INVALID");
        throw Forbidden();
    }

    private static string NormalizeHiddenObjectId(
        string? value,
        string field,
        MeResponse actor)
        => NormalizeScopedObjectId(value, field, actor);

    private static IReadOnlyList<string> BuildPermissionCodes(MeResponse actor)
    {
        var result = new HashSet<string>(StringComparer.Ordinal)
        {
            SummaryPermission
        };
        foreach (var role in actor.Roles ?? [])
        {
            var normalized = role?.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(normalized))
                result.Add($"ROLE:{normalized}");
        }
        if (RoleGuard.IsSystemAdmin(actor) || RoleGuard.IsAdmin(actor))
            result.Add(DetailPermission);
        return result.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static string NormalizeTenantKey(string? value)
        => ObjectId.TryParse(value, out var parsed) ? parsed.ToString() : "NO_UNIT";

    private static string? NormalizeTenantId(string? value)
        => ObjectId.TryParse(value, out var parsed) ? parsed.ToString() : null;

    private static string BuildAuthorizationSnapshotHash(
        MeResponse actor,
        IReadOnlyList<string> permissionCodes,
        WorkAssignment scope)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_RECONCILIATION_AUTHORIZATION_V1",
            actorUserId = actor.Id,
            tenantUnitId = NormalizeTenantId(actor.UnitId),
            workId = scope.WorkId,
            scopeAssignmentId = scope.Id,
            permissionCodes,
            policy = "AUTHORIZED_SCOPE_SERVER_DERIVED_V1"
        });

    private static string BuildSourceLifecycleHash(WorkReportStatisticRebuildJob p9)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_SOURCE_LIFECYCLE_PIN_V1",
            sourceReportId = p9.SourceReportId,
            sourcePayloadRevision = p9.SourcePayloadRevision,
            sourcePayloadHash = p9.SourcePayloadHash,
            sourceLifecycleRevision = p9.SourceLifecycleRevision,
            sourceLifecycleEventKey = p9.SourceLifecycleEventKey,
            sourceLifecycleStatus = p9.SourceStatus,
            sourceMembershipSignature = p9.SourceMembershipSignature
        });

    private static string BuildP8ConfigBundleHash(WorkReportStatisticRebuildJob p9)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_P8_CONFIG_BUNDLE_PIN_V1",
            ownerId = p9.DynamicFormTemplateId,
            configId = p9.ConfigId,
            configVersionId = p9.ConfigVersionId,
            configVersionNo = p9.ConfigVersionNo,
            configRevision = p9.ConfigRevision,
            configHash = p9.ConfigHash
        });

    private static string BuildPageContractHash()
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_RECONCILIATION_PAGE_CONTRACT_V1",
            defaultPage = 1,
            defaultPageSize = 25,
            maxPageSize = 200,
            totals = "FULL_FILTER",
            order = new[] { "createdAtUtc:desc", "id:desc" }
        });

    private static string DeriveTimeAxis(WorkReportStatisticRebuildJob p9, string grain)
        => string.IsNullOrWhiteSpace(p9.PeriodKind)
            ? grain
            : p9.PeriodKind.Trim().ToUpperInvariant();

    private static string? DeriveContributionProvenanceHash(
        WorkReportStatisticRebuildJob p9)
    {
        if (StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                p9.FlowContributionLedgerHash))
        {
            return p9.FlowContributionLedgerHash;
        }
        if (string.IsNullOrWhiteSpace(p9.FlowInstanceId))
            return null;
        return StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_FLOW_CONTRIBUTION_PROVENANCE_V1",
            p9.FlowTemplateVersionId,
            p9.FlowPayloadHash,
            p9.FlowInstanceId,
            p9.FlowExecutionEpoch,
            p9.FlowStepInstanceId,
            p9.FlowBranchId,
            p9.FlowContributionPolicy,
            p9.FlowContributionPolicyHash,
            p9.FlowContributionOperationVersion
        });
    }

    private static object BuildCreateRequestBinding(
        string workId,
        string scopeAssignmentId,
        NormalizedCreate request,
        string? actualCapturePlanSha256 = null,
        string? actualConfigurationBundleSha256 = null)
    {
        if (request.ActualCapturePlan is null)
            return new CreateRequestBinding(
                "P10_RECONCILIATION_CREATE_REQUEST_V1",
                workId,
                scopeAssignmentId,
                request.CommandId,
                request.P9ResultKind,
                request.P9ResultId,
                request.P9RunId,
                request.ConceptKey,
                request.Grain,
                request.FilterHash);
        if (RequiresV4RequestBinding(request.ActualCapturePlan))
            return new CreateRequestBindingV3(
                "P10_RECONCILIATION_CREATE_REQUEST_V3",
                workId,
                scopeAssignmentId,
                request.CommandId,
                request.P9ResultKind,
                request.P9ResultId,
                request.P9RunId,
                request.ConceptKey,
                request.Grain,
                request.FilterHash,
                request.CanonicalFilterJson ?? string.Empty,
                BuildActualCapturePlanRequestBindingV4(
                    request.ActualCapturePlan),
                actualCapturePlanSha256 ?? string.Empty,
                actualConfigurationBundleSha256 ?? string.Empty);
        return new CreateRequestBindingV2(
            "P10_RECONCILIATION_CREATE_REQUEST_V2",
            workId,
            scopeAssignmentId,
            request.CommandId,
            request.P9ResultKind,
            request.P9ResultId,
            request.P9RunId,
            request.ConceptKey,
            request.Grain,
            request.FilterHash,
            request.CanonicalFilterJson ?? string.Empty,
            BuildActualCapturePlanRequestBinding(request.ActualCapturePlan),
            actualCapturePlanSha256 ?? string.Empty,
            actualConfigurationBundleSha256 ?? string.Empty);
    }

    private static object BuildCreateRequestBinding(
        StatisticReconciliationRun run)
    {
        if (run.ActualCapturePlan is null)
            return new CreateRequestBinding(
                "P10_RECONCILIATION_CREATE_REQUEST_V1",
                run.WorkId,
                run.ScopeAssignmentId,
                run.CommandId,
                run.P9ResultKind,
                run.P9ResultId,
                run.P9RunId,
                run.ConceptKey,
                run.Grain,
                run.FilterHash);
        if (StatisticReconciliationActualCapturePlanIntegrity.IsV4(
                run.ActualCapturePlan))
        {
            return new CreateRequestBindingV3(
                "P10_RECONCILIATION_CREATE_REQUEST_V3",
                run.WorkId,
                run.ScopeAssignmentId,
                run.CommandId,
                run.P9ResultKind,
                run.P9ResultId,
                run.P9RunId,
                run.ConceptKey,
                run.Grain,
                run.FilterHash,
                run.CanonicalFilterJson ?? string.Empty,
                BuildActualCapturePlanRequestBindingV4(
                    run.ActualCapturePlan),
                run.ActualCapturePlanSha256 ?? string.Empty,
                run.ActualConfigurationBundleSha256 ?? string.Empty);
        }
        return new CreateRequestBindingV2(
            "P10_RECONCILIATION_CREATE_REQUEST_V2",
            run.WorkId,
            run.ScopeAssignmentId,
            run.CommandId,
            run.P9ResultKind,
            run.P9ResultId,
            run.P9RunId,
            run.ConceptKey,
            run.Grain,
            run.FilterHash,
            run.CanonicalFilterJson ?? string.Empty,
            BuildActualCapturePlanRequestBinding(run.ActualCapturePlan),
            run.ActualCapturePlanSha256 ?? string.Empty,
            run.ActualConfigurationBundleSha256 ?? string.Empty);
    }

    private static bool RequiresV4RequestBinding(
        NormalizedActualCapturePlanRequest request)
        => !StatisticReconciliationActualCapturePlanIntegrity.IsConfigured(
                request.BasicDisposition) ||
           !StatisticReconciliationActualCapturePlanIntegrity.IsConfigured(
                request.AdvancedDisposition) ||
           !StatisticReconciliationActualCapturePlanIntegrity.IsConfigured(
                request.DiffDisposition);

    private static ActualCapturePlanRequestBinding
        BuildActualCapturePlanRequestBinding(
            NormalizedActualCapturePlanRequest request)
        => new(
            request.BoundaryRegistryVersion,
            request.BasicSnapshotId,
            request.BasicMode,
            request.AdvancedSectionId,
            request.AdvancedDayNodeIds,
            request.AdvancedMonthNodeIds,
            request.AdvancedYearNodeIds,
            request.DiffResultId,
            request.DiffRunId,
            request.ApiSurface,
            request.ApiOwnerResultId,
            request.ExportId);

    private static ActualCapturePlanRequestBinding
        BuildActualCapturePlanRequestBinding(
            StatisticReconciliationActualCapturePlan plan)
        => new(
            plan.BoundaryRegistryVersion,
            plan.Basic.SnapshotId,
            plan.Basic.Mode,
            plan.Advanced.SectionId,
            plan.Advanced.DayNodeIds,
            plan.Advanced.MonthNodeIds,
            plan.Advanced.YearNodeIds,
            plan.Diff.ResultId,
            plan.Diff.RunId,
            plan.Api.Surface,
            plan.Api.OwnerResultId!,
            plan.Export.ExportId);

    private static ActualCapturePlanRequestBindingV4
        BuildActualCapturePlanRequestBindingV4(
            NormalizedActualCapturePlanRequest request)
        => new(
            request.BoundaryRegistryVersion,
            request.BasicSnapshotId,
            request.BasicMode,
            request.AdvancedSectionId,
            request.AdvancedDayNodeIds,
            request.AdvancedMonthNodeIds,
            request.AdvancedYearNodeIds,
            request.DiffResultId,
            request.DiffRunId,
            request.ApiSurface,
            request.ApiOwnerResultId,
            request.ExportId,
            request.BasicDisposition,
            request.AdvancedDisposition,
            request.DiffDisposition);

    private static ActualCapturePlanRequestBindingV4
        BuildActualCapturePlanRequestBindingV4(
            StatisticReconciliationActualCapturePlan plan)
        => new(
            plan.BoundaryRegistryVersion,
            plan.Basic.SnapshotId,
            plan.Basic.Mode,
            plan.Advanced.SectionId,
            plan.Advanced.DayNodeIds,
            plan.Advanced.MonthNodeIds,
            plan.Advanced.YearNodeIds,
            plan.Diff.ResultId,
            plan.Diff.RunId,
            plan.Api.Surface,
            plan.Api.OwnerResultId!,
            plan.Export.ExportId,
            plan.Basic.Disposition,
            plan.Advanced.Disposition,
            plan.Diff.Disposition);
    private static string BuildReceiptId(
        MeResponse actor,
        string workId,
        string scopeAssignmentId,
        string commandId)
        => StatisticReconciliationCanonicalJson.HashText(string.Join(
            "\n",
            "P10_RECONCILIATION_RECEIPT_V1",
            actor.Id,
            NormalizeTenantKey(actor.UnitId),
            workId,
            scopeAssignmentId,
            commandId));

    private static string BuildImmutableIdentityHash(StatisticReconciliationRun run)
    {
        var baseSha = BuildBaseImmutableIdentityHash(run);
        return run.ActualCapturePlan is null
            ? baseSha
            : StatisticReconciliationCanonicalJson.HashObject(new
            {
                schema = "P10_RECONCILIATION_IDENTITY_V2",
                baseImmutableIdentitySha256 = baseSha,
                run.CanonicalFilterJson,
                run.ActualCapturePlanSha256,
                run.ActualConfigurationBundleSha256
            });
    }

    private static string BuildBaseImmutableIdentityHash(StatisticReconciliationRun run)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_RECONCILIATION_IDENTITY_V1",
            run.ActorUserId,
            run.TenantUnitId,
            run.AuthorizationSnapshotHash,
            run.WorkId,
            run.ScopeAssignmentId,
            run.P9ResultKind,
            run.P9ResultId,
            run.P9RunId,
            run.P9GenerationId,
            run.P9GenerationHash,
            run.P9RunKind,
            run.P9CapabilityId,
            run.P9RouteId,
            run.P9CandidateChainId,
            run.P9CandidatePromptId,
            run.SourceReportId,
            run.SourcePayloadRevision,
            run.SourcePayloadHash,
            run.SourceLifecycleRevision,
            run.SourceLifecycleEventKey,
            run.SourceLifecycleHash,
            run.SourceLifecycleStatus,
            run.DynamicFormFamilyId,
            run.DynamicFormVersionId,
            run.DynamicFormVersionNo,
            run.DynamicFormSchemaHash,
            run.FlowTemplateId,
            run.FlowFamilyRevision,
            run.FlowTemplateVersionId,
            run.FlowPayloadHash,
            run.FlowInstanceId,
            run.FlowInstanceRevision,
            run.FlowExecutionEpoch,
            run.FlowExecutionEpochId,
            run.FlowExecutionEpochRevision,
            run.FlowStepId,
            run.FlowBranchId,
            run.FlowStepInstanceId,
            run.FlowStepInstanceRevision,
            run.FlowContributionPolicy,
            run.FlowContributionPolicyHash,
            run.FlowEffectiveStatus,
            run.FlowContributionProvenanceHash,
            run.P8ConfigOwnerId,
            run.P8ConfigId,
            run.P8ConfigVersionId,
            run.P8ConfigVersionNo,
            run.P8ConfigRevision,
            run.P8ConfigHash,
            run.P8ConfigBundleHash,
            run.P9CatalogVersion,
            run.P9CatalogRawSha256,
            run.P9CatalogSemanticSha256,
            run.P9SchemaRawSha256,
            run.P9SchemaSemanticSha256,
            run.P9StageLockSha256,
            run.CandidateChainId,
            run.CandidateCatalogVersion,
            run.CandidateCatalogRawSha256,
            run.CandidateCatalogSemanticSha256,
            run.CandidateSchemaRawSha256,
            run.CandidateSchemaSemanticSha256,
            run.CandidateStageLockSha256,
            run.PeriodKey,
            run.PeriodInstanceKey,
            run.PeriodKind,
            periodStartUtc = FormatUtc(run.PeriodStartUtc),
            periodEndUtc = FormatUtc(run.PeriodEndUtc),
            run.ConceptKey,
            run.Grain,
            run.TimeAxis,
            run.FilterHash,
            run.PageContractHash,
            run.SourceSetSha256,
            run.ExpectedAlgorithmRevision,
            run.ExpectedAlgorithmSha256
        });

    private static string BuildImmutableHeaderHash(StatisticReconciliationRun run)
    {
        var baseSha = BuildBaseImmutableHeaderHash(run);
        return run.ActualCapturePlan is null
            ? baseSha
            : StatisticReconciliationCanonicalJson.HashObject(new
            {
                schema = "P10_RECONCILIATION_IMMUTABLE_HEADER_V2",
                baseImmutableHeaderSha256 = baseSha,
                run.CanonicalFilterJson,
                run.ActualCapturePlanSha256,
                run.ActualConfigurationBundleSha256
            });
    }

    private static string BuildBaseImmutableHeaderHash(StatisticReconciliationRun run)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_RECONCILIATION_IMMUTABLE_HEADER_V1",
            run.Id,
            run.ReceiptId,
            run.CommandId,
            run.RequestHash,
            run.ImmutableIdentityHash,
            run.ActorUserId,
            run.TenantUnitId,
            permissionCodes = run.PermissionCodes.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            run.AuthorizationSnapshotHash,
            run.WorkId,
            run.ScopeAssignmentId,
            run.P9ResultKind,
            run.P9ResultId,
            run.P9RunId,
            run.P9GenerationId,
            run.P9GenerationHash,
            run.P9RunKind,
            run.P9CapabilityId,
            run.P9RouteId,
            run.P9CandidateChainId,
            run.P9CandidatePromptId,
            run.SourceReportId,
            run.SourcePayloadRevision,
            run.SourcePayloadHash,
            run.SourceLifecycleRevision,
            run.SourceLifecycleEventKey,
            run.SourceLifecycleHash,
            run.SourceLifecycleStatus,
            run.DynamicFormFamilyId,
            run.DynamicFormVersionId,
            run.DynamicFormVersionNo,
            run.DynamicFormSchemaHash,
            run.FlowTemplateId,
            run.FlowFamilyRevision,
            run.FlowTemplateVersionId,
            run.FlowPayloadHash,
            run.FlowInstanceId,
            run.FlowInstanceRevision,
            run.FlowExecutionEpoch,
            run.FlowExecutionEpochId,
            run.FlowExecutionEpochRevision,
            run.FlowStepId,
            run.FlowBranchId,
            run.FlowStepInstanceId,
            run.FlowStepInstanceRevision,
            run.FlowContributionPolicy,
            run.FlowContributionPolicyHash,
            run.FlowEffectiveStatus,
            run.FlowContributionProvenanceHash,
            run.P8ConfigOwnerId,
            run.P8ConfigId,
            run.P8ConfigVersionId,
            run.P8ConfigVersionNo,
            run.P8ConfigRevision,
            run.P8ConfigHash,
            run.P8ConfigBundleHash,
            run.P9CatalogVersion,
            run.P9CatalogRawSha256,
            run.P9CatalogSemanticSha256,
            run.P9SchemaRawSha256,
            run.P9SchemaSemanticSha256,
            run.P9StageLockSha256,
            run.CandidateChainId,
            run.CandidatePromptId,
            run.CandidateStage,
            run.CandidateCatalogVersion,
            run.CandidateCatalogRawSha256,
            run.CandidateCatalogSemanticSha256,
            run.CandidateSchemaRawSha256,
            run.CandidateSchemaSemanticSha256,
            run.CandidateStageLockSha256,
            run.PeriodKey,
            run.PeriodInstanceKey,
            run.PeriodKind,
            periodStartUtc = FormatUtc(run.PeriodStartUtc),
            periodEndUtc = FormatUtc(run.PeriodEndUtc),
            run.ConceptKey,
            run.Grain,
            run.TimeAxis,
            run.FilterHash,
            run.PageContractHash,
            run.SourceSetSha256,
            run.ExpectedAlgorithmRevision,
            run.ExpectedAlgorithmSha256,
            run.MaxRetryCount,
            deadlineAtUtc = FormatUtc(
                run.InitialDeadlineAtUtc ?? run.DeadlineAtUtc),
            createdAtUtc = FormatUtc(run.CreatedAtUtc),
            run.CreatedByUserId
        });

    private static string BuildAcceptedResponseHash(StatisticReconciliationRun run)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_RECONCILIATION_ACCEPTED_RESPONSE_V1",
            reconciliationId = run.Id,
            run.ReceiptId,
            run.CommandId,
            run.RequestHash,
            run.ImmutableIdentityHash,
            run.ImmutableHeaderHash,
            acceptedAtUtc = FormatUtc(run.CreatedAtUtc)
        });

    private static ReconciliationState StateOf(StatisticReconciliationRun run)
        => new(
            run.Status,
            run.StateRevision,
            run.RetryCount,
            run.NextRetryAtUtc,
            run.LeaseOwnerId,
            run.ClaimToken,
            run.LeaseUntilUtc,
            run.LastHeartbeatAtUtc,
            run.DeadlineAtUtc,
            run.DiagnosticCode,
            run.PendingGenerationId,
            run.PendingGenerationHash,
            run.PendingGenerationPublishedAtUtc,
            run.CurrentGenerationId,
            run.CurrentGenerationHash,
            run.Recheck,
            run.CurrentGenerationRecheckCaptureBinding?.BindingSha256,
            run.CurrentRecheckFinalizeReceipt?.ReceiptSha256,
            run.ReviewDecisionRevision,
            run.GenerationPublishRevision,
            run.OperationReceiptHistoryHash,
            run.RecheckBeginReceiptHistoryHash,
            run.CancelledAtUtc,
            run.FailedAtUtc);

    private static string BuildStateHash(
        string reconciliationId,
        ReconciliationState state)
    {
        if (state.Recheck is null &&
            state.RecheckBeginReceiptHistoryHash is null &&
            state.CurrentGenerationRecheckCaptureBindingSha256 is null &&
            state.CurrentRecheckFinalizeReceiptSha256 is null &&
            state.ReviewDecisionRevision == 0)
            return StatisticReconciliationCanonicalJson.HashObject(new
            {
                schema = "P10_RECONCILIATION_STATE_V1",
                reconciliationId,
                state.Status,
                state.StateRevision,
                state.RetryCount,
                nextRetryAtUtc = FormatUtc(state.NextRetryAtUtc),
                state.LeaseOwnerId,
                state.ClaimToken,
                leaseUntilUtc = FormatUtc(state.LeaseUntilUtc),
                lastHeartbeatAtUtc = FormatUtc(state.LastHeartbeatAtUtc),
                deadlineAtUtc = FormatUtc(state.DeadlineAtUtc),
                state.DiagnosticCode,
                state.PendingGenerationId,
                state.PendingGenerationHash,
                pendingGenerationPublishedAtUtc = FormatUtc(state.PendingGenerationPublishedAtUtc),
                state.CurrentGenerationId,
                state.CurrentGenerationHash,
                state.GenerationPublishRevision,
                state.OperationReceiptHistoryHash,
                cancelledAtUtc = FormatUtc(state.CancelledAtUtc),
                failedAtUtc = FormatUtc(state.FailedAtUtc)
            });

        if (state.CurrentRecheckFinalizeReceiptSha256 is null &&
            state.ReviewDecisionRevision == 0)
            return StatisticReconciliationCanonicalJson.HashObject(new
            {
                schema = "P10_RECONCILIATION_STATE_RECHECK_V2",
            reconciliationId,
            state.Status,
            state.StateRevision,
            state.RetryCount,
            nextRetryAtUtc = FormatUtc(state.NextRetryAtUtc),
            state.LeaseOwnerId,
            state.ClaimToken,
            leaseUntilUtc = FormatUtc(state.LeaseUntilUtc),
            lastHeartbeatAtUtc = FormatUtc(state.LastHeartbeatAtUtc),
            deadlineAtUtc = FormatUtc(state.DeadlineAtUtc),
            state.DiagnosticCode,
            state.PendingGenerationId,
            state.PendingGenerationHash,
            pendingGenerationPublishedAtUtc = FormatUtc(state.PendingGenerationPublishedAtUtc),
            state.CurrentGenerationId,
            state.CurrentGenerationHash,
            state.GenerationPublishRevision,
            state.OperationReceiptHistoryHash,
            state.RecheckBeginReceiptHistoryHash,
            state.CurrentGenerationRecheckCaptureBindingSha256,
            recheckMarkerStateHash = state.Recheck?.MarkerStateHash,
            cancelledAtUtc = FormatUtc(state.CancelledAtUtc),
            failedAtUtc = FormatUtc(state.FailedAtUtc)
        });

        return StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_RECONCILIATION_STATE_RECHECK_V3",
            reconciliationId,
            state.Status,
            state.StateRevision,
            state.RetryCount,
            nextRetryAtUtc = FormatUtc(state.NextRetryAtUtc),
            state.LeaseOwnerId,
            state.ClaimToken,
            leaseUntilUtc = FormatUtc(state.LeaseUntilUtc),
            lastHeartbeatAtUtc = FormatUtc(state.LastHeartbeatAtUtc),
            deadlineAtUtc = FormatUtc(state.DeadlineAtUtc),
            state.DiagnosticCode,
            state.PendingGenerationId,
            state.PendingGenerationHash,
            pendingGenerationPublishedAtUtc = FormatUtc(state.PendingGenerationPublishedAtUtc),
            state.CurrentGenerationId,
            state.CurrentGenerationHash,
            state.GenerationPublishRevision,
            state.OperationReceiptHistoryHash,
            state.RecheckBeginReceiptHistoryHash,
            state.CurrentGenerationRecheckCaptureBindingSha256,
            state.CurrentRecheckFinalizeReceiptSha256,
            state.ReviewDecisionRevision,
            recheckMarkerStateHash = state.Recheck?.MarkerStateHash,
            cancelledAtUtc = FormatUtc(state.CancelledAtUtc),
            failedAtUtc = FormatUtc(state.FailedAtUtc)
        });
    }
    private static string BuildOperationReceiptHash(
        StatisticReconciliationOperationReceipt receipt)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_RECONCILIATION_OPERATION_RECEIPT_V1",
            receipt.ReceiptId,
            receipt.ReconciliationId,
            receipt.ActorUserId,
            receipt.Operation,
            receipt.CommandId,
            receipt.RequestHash,
            receipt.ExpectedStateRevision,
            receipt.ExpectedStateHash,
            receipt.AcceptedStateRevision,
            receipt.AcceptedStatus,
            acceptedAtUtc = FormatUtc(receipt.AcceptedAtUtc)
        });

    private static string BuildOperationReceiptHistoryHash(
        IEnumerable<StatisticReconciliationOperationReceipt> receipts)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_RECONCILIATION_OPERATION_HISTORY_V1",
            receipts = receipts.Select(receipt => new
            {
                receipt.ReceiptHash,
                computedReceiptHash = BuildOperationReceiptHash(receipt)
            }).ToArray()
        });

    private static bool HasValidStateSemantics(StatisticReconciliationRun run)
    {
        if (run.StateRevision < 1 ||
            run.RetryCount < 0 ||
            run.MaxRetryCount is < 1 or > 20 ||
            run.RetryCount > run.MaxRetryCount ||
            run.GenerationPublishRevision < 0 ||
            run.DeadlineAtUtc == default ||
            (run.NextRetryAtUtc.HasValue &&
             run.NextRetryAtUtc.Value >= run.DeadlineAtUtc) ||
            (run.LeaseUntilUtc.HasValue &&
             run.LeaseUntilUtc.Value > run.DeadlineAtUtc) ||
            (run.DiagnosticCode is not null &&
             !IsIntegrityToken(run.DiagnosticCode, 128)))
        {
            return false;
        }

        var pendingAbsent = run.PendingGenerationId is null &&
                            run.PendingGenerationHash is null &&
                            !run.PendingGenerationPublishedAtUtc.HasValue;
        var pendingComplete =
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                run.PendingGenerationId) &&
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                run.PendingGenerationHash) &&
            run.PendingGenerationPublishedAtUtc.HasValue &&
            run.PendingGenerationPublishedAtUtc.Value != default;
        if (!pendingAbsent && !pendingComplete)
            return false;

        var currentAbsent = run.CurrentGenerationId is null &&
                            run.CurrentGenerationHash is null;
        var currentComplete =
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                run.CurrentGenerationId) &&
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                run.CurrentGenerationHash);
        if (!currentAbsent && !currentComplete)
            return false;
        if (!pendingAbsent && !currentAbsent && run.Recheck is null)
            return false;
        if ((!pendingAbsent || !currentAbsent) &&
            run.GenerationPublishRevision < 1)
        {
            return false;
        }

        var noWorkerFence = run.LeaseOwnerId is null &&
                            run.ClaimToken is null &&
                            !run.LeaseUntilUtc.HasValue &&
                            !run.LastHeartbeatAtUtc.HasValue;
        var completeWorkerFence =
            IsIntegrityToken(run.LeaseOwnerId, 128) &&
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                run.ClaimToken) &&
            run.LeaseUntilUtc.HasValue &&
            run.LastHeartbeatAtUtc.HasValue;
        var noTerminalTimestamp = !run.CancelledAtUtc.HasValue &&
                                  !run.FailedAtUtc.HasValue;

        if (run.Recheck is not null)
        {
            try
            {
                Recheck.StatisticReconciliationRecheckCanonical
                    .RequireValidMarker(run.Recheck);
            }
            catch (Recheck.StatisticReconciliationRecheckException)
            {
                return false;
            }

            var marker = run.Recheck;
            var baseCurrent = currentComplete &&
                string.Equals(run.CurrentGenerationId,
                    marker.BaseCurrentGenerationId,
                    StringComparison.Ordinal) &&
                string.Equals(run.CurrentGenerationHash,
                    marker.BaseCurrentGenerationHash,
                    StringComparison.Ordinal);
            return marker.Phase switch
            {
                StatisticReconciliationRecheckPhases.ReadyToClaim =>
                    run.Status == StatisticReconciliationRunStatuses.Queued &&
                    baseCurrent && pendingAbsent && noWorkerFence &&
                    noTerminalTimestamp &&
                    run.RetryCount < run.MaxRetryCount,
                StatisticReconciliationRecheckPhases.CaptureRunning =>
                    run.Status == StatisticReconciliationRunStatuses.Running &&
                    baseCurrent && pendingAbsent && completeWorkerFence &&
                    !run.NextRetryAtUtc.HasValue && noTerminalTimestamp &&
                    run.RetryCount < run.MaxRetryCount,
                StatisticReconciliationRecheckPhases.PendingPublished =>
                    run.Status == StatisticReconciliationRunStatuses.Queued &&
                    baseCurrent && pendingComplete && noWorkerFence &&
                    noTerminalTimestamp &&
                    string.Equals(run.PendingGenerationId,
                        marker.SuccessorGenerationId,
                        StringComparison.Ordinal) &&
                    string.Equals(run.PendingGenerationHash,
                        marker.SuccessorGenerationHash,
                        StringComparison.Ordinal),
                StatisticReconciliationRecheckPhases
                    .ReviewSupersessionPending =>
                    Recheck.StatisticReconciliationRecheckTerminalStatuses.All
                        .Contains(run.Status) &&
                    currentComplete && pendingAbsent && noWorkerFence &&
                    !run.NextRetryAtUtc.HasValue &&
                    string.Equals(run.CurrentGenerationId,
                        marker.SuccessorGenerationId,
                        StringComparison.Ordinal) &&
                    string.Equals(run.CurrentGenerationHash,
                        marker.SuccessorGenerationHash,
                        StringComparison.Ordinal) &&
                    (run.Status == StatisticReconciliationRunStatuses.Failed
                        ? !run.CancelledAtUtc.HasValue && run.FailedAtUtc.HasValue
                        : noTerminalTimestamp),
                _ => false
            };
        }

        return run.Status switch
        {
            StatisticReconciliationRunStatuses.Queued =>
                noWorkerFence &&
                noTerminalTimestamp &&
                run.RetryCount < run.MaxRetryCount,
            StatisticReconciliationRunStatuses.Running =>
                completeWorkerFence &&
                !run.NextRetryAtUtc.HasValue &&
                noTerminalTimestamp &&
                pendingAbsent &&
                currentAbsent &&
                run.RetryCount < run.MaxRetryCount,
            StatisticReconciliationRunStatuses.Matched or
            StatisticReconciliationRunStatuses.Mismatched or
            StatisticReconciliationRunStatuses.Stale =>
                noWorkerFence &&
                !run.NextRetryAtUtc.HasValue &&
                noTerminalTimestamp &&
                pendingAbsent &&
                currentComplete,
            StatisticReconciliationRunStatuses.Failed =>
                noWorkerFence &&
                !run.NextRetryAtUtc.HasValue &&
                !run.CancelledAtUtc.HasValue &&
                run.FailedAtUtc.HasValue,
            StatisticReconciliationRunStatuses.Cancelled =>
                noWorkerFence &&
                !run.NextRetryAtUtc.HasValue &&
                run.CancelledAtUtc.HasValue &&
                !run.FailedAtUtc.HasValue,
            _ => false
        };
    }

    private static bool HasValidOperationReceiptSemantics(
        StatisticReconciliationRun run,
        StatisticReconciliationOperationReceipt receipt)
    {
        if (!StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                receipt.ReceiptId) ||
            !ObjectId.TryParse(receipt.ActorUserId, out _) ||
            !string.Equals(receipt.ReconciliationId, run.Id, StringComparison.Ordinal) ||
            !string.Equals(receipt.Operation, "CANCEL", StringComparison.Ordinal) ||
            !IsIntegrityText(receipt.CommandId, 128) ||
            receipt.ExpectedStateRevision < 1 ||
            receipt.ExpectedStateRevision == long.MaxValue ||
            receipt.AcceptedStateRevision != receipt.ExpectedStateRevision + 1 ||
            receipt.AcceptedStateRevision > run.StateRevision ||
            !string.Equals(
                receipt.AcceptedStatus,
                StatisticReconciliationRunStatuses.Cancelled,
                StringComparison.Ordinal) ||
            receipt.AcceptedAtUtc == default)
        {
            return false;
        }

        var expectedReceiptId = StatisticReconciliationCanonicalJson.HashText(
            string.Join(
                "\n",
                "P10_RECONCILIATION_OPERATION_RECEIPT_V1",
                run.Id,
                receipt.ActorUserId,
                receipt.Operation,
                receipt.CommandId));
        var expectedRequestHash = StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_RECONCILIATION_CANCEL_REQUEST_V1",
            reconciliationId = run.Id,
            actorUserId = receipt.ActorUserId,
            commandId = receipt.CommandId,
            receipt.ExpectedStateRevision,
            receipt.ExpectedStateHash
        });
        return string.Equals(
                   receipt.ReceiptId,
                   expectedReceiptId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   receipt.RequestHash,
                   expectedRequestHash,
                   StringComparison.Ordinal);
    }

    private static bool IsIntegrityText(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Length <= maxLength &&
           string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
           !value.Any(char.IsControl);

    private static bool IsIntegrityToken(string? value, int maxLength)
        => IsIntegrityText(value, maxLength) &&
           value!.All(character =>
               char.IsAsciiLetterOrDigit(character) ||
               character is '_' or '-' or '.' or ':' or '/');

    private static bool HasValidDirectP8ConfigBundle(
        StatisticReconciliationRun run)
        => string.Equals(
            run.P8ConfigBundleHash,
            StatisticReconciliationCanonicalJson.HashObject(new
            {
                schema = "P10_P8_CONFIG_BUNDLE_PIN_V1",
                ownerId = run.P8ConfigOwnerId,
                configId = run.P8ConfigId,
                configVersionId = run.P8ConfigVersionId,
                configVersionNo = run.P8ConfigVersionNo,
                configRevision = run.P8ConfigRevision,
                configHash = run.P8ConfigHash
            }),
            StringComparison.Ordinal);

    private static bool HasValidActualCapturePlanBinding(
        StatisticReconciliationRun run)
    {
        if (run.ActualCapturePlan is null)
        {
            return run.CanonicalFilterJson is null &&
                   run.ActualCapturePlanSha256 is null &&
                   run.ActualConfigurationBundleSha256 is null;
        }
        if (string.IsNullOrWhiteSpace(run.CanonicalFilterJson) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                run.ActualCapturePlanSha256) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                run.ActualConfigurationBundleSha256) ||
            !string.Equals(
                run.ActualCapturePlan.ActualConfigurationBundleSha256,
                run.ActualConfigurationBundleSha256,
                StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(
                run.CanonicalFilterJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !string.Equals(
                    StatisticReconciliationCanonicalJson.Canonicalize(
                        document.RootElement),
                    run.CanonicalFilterJson,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    StatisticReconciliationCanonicalJson.HashText(
                        run.CanonicalFilterJson),
                    run.FilterHash,
                    StringComparison.Ordinal))
            {
                return false;
            }
            StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
                run.ActualCapturePlan,
                run.ActualCapturePlanSha256);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
    private static void RequireReadIntegrity(StatisticReconciliationRun run)
    {
        if (!StatisticReconciliationRunStatuses.All.Contains(run.Status) ||
            !HasValidStateSemantics(run) ||
            run.SourceSetSha256 is not null ||
            run.ExpectedAlgorithmRevision is not null ||
            run.ExpectedAlgorithmSha256 is not null ||
            !HasValidDirectP8ConfigBundle(run) ||
            !HasValidActualCapturePlanBinding(run) ||
            !HasValidRecheckBeginReceiptHistory(run) ||
            !HasValidCurrentRecheckCaptureBinding(run) ||
            !HasValidRecheckFinalizeReceipt(run) ||
            run.ReviewDecisionRevision < 0 ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(run.RequestHash) ||
            !string.Equals(
                run.RequestHash,
                StatisticReconciliationCanonicalJson.HashObject(
                    BuildCreateRequestBinding(run)),
                StringComparison.Ordinal) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(run.ImmutableIdentityHash) ||
            !string.Equals(
                run.ImmutableIdentityHash,
                BuildImmutableIdentityHash(run),
                StringComparison.Ordinal) ||
            run.PermissionCodes is null ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(run.ImmutableHeaderHash) ||
            !string.Equals(
                run.ImmutableHeaderHash,
                BuildImmutableHeaderHash(run),
                StringComparison.Ordinal) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(run.ReceiptResponseHash) ||
            !string.Equals(
                run.ReceiptResponseHash,
                BuildAcceptedResponseHash(run),
                StringComparison.Ordinal))
        {
            throw JobConflict("IMMUTABLE_INTEGRITY_INVALID");
        }

        var receipts = run.OperationReceipts ?? [];
        if (receipts.Count > MaxOperationReceipts ||
            receipts.Any(receipt =>
                !HasValidOperationReceiptSemantics(run, receipt) ||
                !StatisticReconciliationCanonicalJson.IsCanonicalSha256(receipt.ReceiptHash) ||
                !string.Equals(
                    receipt.ReceiptHash,
                    BuildOperationReceiptHash(receipt),
                    StringComparison.Ordinal) ||
                !string.Equals(receipt.ReconciliationId, run.Id, StringComparison.Ordinal) ||
                !StatisticReconciliationCanonicalJson.IsCanonicalSha256(receipt.RequestHash) ||
                !StatisticReconciliationCanonicalJson.IsCanonicalSha256(receipt.ExpectedStateHash) ||
                !StatisticReconciliationCanonicalJson.IsCanonicalSha256(receipt.AcceptedStateHash) ||
                !string.Equals(
                    receipt.AcceptedStateHash,
                    run.StateHash,
                    StringComparison.Ordinal) ||
                receipt.AcceptedStateRevision != run.StateRevision ||
                !string.Equals(
                    receipt.AcceptedStatus,
                    run.Status,
                    StringComparison.Ordinal)) ||
            receipts.GroupBy(receipt => receipt.ReceiptId, StringComparer.Ordinal)
                .Any(group => group.Count() != 1) ||
            receipts.GroupBy(
                    receipt => $"{receipt.ActorUserId}\n{receipt.Operation}\n{receipt.CommandId}",
                    StringComparer.Ordinal)
                .Any(group => group.Count() != 1) ||
            receipts.Zip(receipts.Skip(1), (left, right) =>
                    left.AcceptedStateRevision >= right.AcceptedStateRevision)
                .Any(nonIncreasing => nonIncreasing) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                run.OperationReceiptHistoryHash) ||
            !string.Equals(
                run.OperationReceiptHistoryHash,
                BuildOperationReceiptHistoryHash(receipts),
                StringComparison.Ordinal) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(run.StateHash) ||
            !string.Equals(
                run.StateHash,
                BuildStateHash(run.Id, StateOf(run)),
                StringComparison.Ordinal))
        {
            throw JobConflict("STATE_OR_RECEIPT_INTEGRITY_INVALID");
        }
    }

    private static StatisticReconciliationSummaryResponse ToSummary(
        StatisticReconciliationRun run,
        bool isReplay = false)
        => ToSummary(run, null, isReplay);

    private static StatisticReconciliationSummaryResponse ToSummary(
        StatisticReconciliationRun run,
        StatisticReconciliationPresentationResponse? presentation,
        bool isReplay = false)
        => new()
        {
            ReconciliationId = run.Id,
            ReceiptId = run.ReceiptId,
            CommandId = run.CommandId,
            IsReplay = isReplay,
            WorkId = run.WorkId,
            ScopeAssignmentId = run.ScopeAssignmentId,
            P9ResultKind = run.P9ResultKind,
            Status = run.Status,
            StateRevision = run.StateRevision,
            StateHash = run.StateHash,
            RetryCount = run.RetryCount,
            NextRetryAtUtc = NormalizeUtc(run.NextRetryAtUtc),
            LeaseUntilUtc = NormalizeUtc(run.LeaseUntilUtc),
            DeadlineAtUtc = NormalizeUtc(run.DeadlineAtUtc)!.Value,
            DiagnosticCode = RedactDiagnostic(run.DiagnosticCode),
            HasPendingGeneration = run.PendingGenerationId is not null,
            HasCurrentGeneration = run.CurrentGenerationId is not null,
            CreatedAtUtc = NormalizeUtc(run.CreatedAtUtc)!.Value,
            UpdatedAtUtc = NormalizeUtc(run.UpdatedAtUtc)!.Value,
            Presentation = presentation
        };

    private static StatisticReconciliationDetailResponse ToDetail(
        StatisticReconciliationRun run,
        StatisticReconciliationPresentationResponse? presentation = null)
        => new()
        {
            ReconciliationId = run.Id,
            ReceiptId = run.ReceiptId,
            CommandId = run.CommandId,
            WorkId = run.WorkId,
            ScopeAssignmentId = run.ScopeAssignmentId,
            P9ResultKind = run.P9ResultKind,
            Status = run.Status,
            StateRevision = run.StateRevision,
            StateHash = run.StateHash,
            RetryCount = run.RetryCount,
            NextRetryAtUtc = NormalizeUtc(run.NextRetryAtUtc),
            LeaseUntilUtc = NormalizeUtc(run.LeaseUntilUtc),
            DeadlineAtUtc = NormalizeUtc(run.DeadlineAtUtc)!.Value,
            DiagnosticCode = run.DiagnosticCode,
            HasPendingGeneration = run.PendingGenerationId is not null,
            HasCurrentGeneration = run.CurrentGenerationId is not null,
            CreatedAtUtc = NormalizeUtc(run.CreatedAtUtc)!.Value,
            UpdatedAtUtc = NormalizeUtc(run.UpdatedAtUtc)!.Value,
            Presentation = presentation,
            ActorUserId = run.ActorUserId,
            TenantUnitId = run.TenantUnitId,
            PermissionCodes = run.PermissionCodes.ToArray(),
            AuthorizationSnapshotHash = run.AuthorizationSnapshotHash,
            RequestHash = run.RequestHash,
            ImmutableIdentityHash = run.ImmutableIdentityHash,
            ImmutableHeaderHash = run.ImmutableHeaderHash,
            P9ResultId = run.P9ResultId,
            P9RunId = run.P9RunId,
            P9GenerationId = run.P9GenerationId,
            P9GenerationHash = run.P9GenerationHash,
            P9CapabilityId = run.P9CapabilityId,
            P9RouteId = run.P9RouteId,
            P9CandidateChainId = run.P9CandidateChainId,
            P9CandidatePromptId = run.P9CandidatePromptId,
            P9CatalogVersion = run.P9CatalogVersion,
            P9CatalogRawSha256 = run.P9CatalogRawSha256,
            P9CatalogSemanticSha256 = run.P9CatalogSemanticSha256,
            P9SchemaRawSha256 = run.P9SchemaRawSha256,
            P9SchemaSemanticSha256 = run.P9SchemaSemanticSha256,
            P9StageLockSha256 = run.P9StageLockSha256,
            SourceReportId = run.SourceReportId,
            SourcePayloadRevision = run.SourcePayloadRevision,
            SourcePayloadHash = run.SourcePayloadHash,
            SourceLifecycleRevision = run.SourceLifecycleRevision,
            SourceLifecycleEventKey = run.SourceLifecycleEventKey,
            SourceLifecycleHash = run.SourceLifecycleHash,
            DynamicFormVersionId = run.DynamicFormVersionId,
            DynamicFormFamilyId = run.DynamicFormFamilyId,
            DynamicFormVersionNo = run.DynamicFormVersionNo,
            DynamicFormSchemaHash = run.DynamicFormSchemaHash,
            FlowTemplateId = run.FlowTemplateId,
            FlowTemplateVersionId = run.FlowTemplateVersionId,
            FlowPayloadHash = run.FlowPayloadHash,
            FlowInstanceId = run.FlowInstanceId,
            FlowExecutionEpoch = run.FlowExecutionEpoch,
            FlowStepId = run.FlowStepId,
            FlowStepInstanceId = run.FlowStepInstanceId,
            FlowBranchId = run.FlowBranchId,
            FlowEffectiveStatus = run.FlowEffectiveStatus,
            FlowContributionPolicyHash = run.FlowContributionPolicyHash,
            FlowContributionProvenanceHash = run.FlowContributionProvenanceHash,
            P8ConfigOwnerId = run.P8ConfigOwnerId,
            P8ConfigId = run.P8ConfigId,
            P8ConfigVersionId = run.P8ConfigVersionId,
            P8ConfigRevision = run.P8ConfigRevision,
            P8ConfigHash = run.P8ConfigHash,
            P8ConfigBundleHash = run.P8ConfigBundleHash,
            CandidateChainId = run.CandidateChainId,
            CandidatePromptId = run.CandidatePromptId,
            CandidateStage = run.CandidateStage,
            CandidateCatalogVersion = run.CandidateCatalogVersion,
            CandidateCatalogRawSha256 = run.CandidateCatalogRawSha256,
            CandidateCatalogSemanticSha256 = run.CandidateCatalogSemanticSha256,
            CandidateSchemaRawSha256 = run.CandidateSchemaRawSha256,
            CandidateSchemaSemanticSha256 = run.CandidateSchemaSemanticSha256,
            CandidateStageLockSha256 = run.CandidateStageLockSha256,
            PeriodKey = run.PeriodKey,
            PeriodInstanceKey = run.PeriodInstanceKey,
            PeriodKind = run.PeriodKind,
            ConceptKey = run.ConceptKey,
            Grain = run.Grain,
            TimeAxis = run.TimeAxis,
            PageContractHash = run.PageContractHash,
            FilterHash = run.FilterHash,
            SourceSetSha256 = run.SourceSetSha256,
            ExpectedAlgorithmSha256 = run.ExpectedAlgorithmSha256,
            ExpectedAlgorithmRevision = run.ExpectedAlgorithmRevision,
            PendingGenerationId = run.PendingGenerationId,
            PendingGenerationHash = run.PendingGenerationHash,
            CurrentGenerationId = run.CurrentGenerationId,
            CurrentGenerationHash = run.CurrentGenerationHash,
            GenerationPublishRevision = run.GenerationPublishRevision
        };

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

    private static string? FormatUtc(DateTime? value)
        => NormalizeUtc(value)?.ToString("O", CultureInfo.InvariantCulture);

    private static string? RedactDiagnostic(string? value)
        => value is "P10_TEST_TRANSIENT" or "P10_TEST_TERMINAL" or
               "P10_RECONCILIATION_JOB_TIMEOUT"
            ? value
            : string.IsNullOrWhiteSpace(value)
                ? null
                : "P10_RECONCILIATION_JOB_FAILED";

    private static AppException RequestInvalid(string field, string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.STAT_RECONCILIATION_REQUEST_INVALID,
            new { field, reason, writes = 0 });

    private static AppException Forbidden()
        => new(AppErrorCode.STAT_RECONCILIATION_FORBIDDEN, new { writes = 0 });

    private static AppException NotFound()
        => new(AppErrorCode.STAT_RECONCILIATION_NOT_FOUND, new { writes = 0 });

    private static AppException HiddenSourceFailure(MeResponse actor, string reason)
        => RoleGuard.IsSystemAdmin(actor)
            ? new AppException(
                AppErrorCode.STAT_RECONCILIATION_REQUEST_INVALID,
                new { reason, writes = 0 })
            : Forbidden();

    private static AppException ReplayMismatch(string reason)
        => new(
            AppErrorCode.STAT_RECONCILIATION_COMMAND_REPLAY_MISMATCH,
            new { reason, writes = 0 });

    private static AppException IdentityConflict(string reason)
        => new(
            AppErrorCode.STAT_RECONCILIATION_IDENTITY_CONFLICT,
            new { reason, writes = 0 });

    private static AppException RevisionConflict(string reason)
        => new(
            AppErrorCode.STAT_RECONCILIATION_REVISION_CONFLICT,
            new { reason, writes = 0 });

    private static AppException JobConflict(string reason, int writes = 0)
        => new(
            AppErrorCode.STAT_RECONCILIATION_JOB_CONFLICT,
            new { reason, writes });

    private sealed record NormalizedCreate(
        string CommandId,
        string P9ResultKind,
        string P9ResultId,
        string P9RunId,
        string ConceptKey,
        string Grain,
        string FilterHash,
        string? CanonicalFilterJson,
        NormalizedActualCapturePlanRequest? ActualCapturePlan,
        string? CapturePlanToken = null);

    private sealed record NormalizedActualCapturePlanRequest(
        string BoundaryRegistryVersion,
        string BasicSnapshotId,
        string BasicMode,
        string AdvancedSectionId,
        IReadOnlyList<string> AdvancedDayNodeIds,
        IReadOnlyList<string> AdvancedMonthNodeIds,
        IReadOnlyList<string> AdvancedYearNodeIds,
        string DiffResultId,
        string DiffRunId,
        string ApiSurface,
        string ApiOwnerResultId,
        string ExportId,
        string BasicDisposition =
            StatisticReconciliationActualSummaryTargetDispositions.Configured,
        string AdvancedDisposition =
            StatisticReconciliationActualSummaryTargetDispositions.Configured,
        string DiffDisposition =
            StatisticReconciliationActualSummaryTargetDispositions.Configured);

    private sealed record CreateRequestBinding(
        string Schema,
        string WorkId,
        string ScopeAssignmentId,
        string CommandId,
        string P9ResultKind,
        string P9ResultId,
        string P9RunId,
        string ConceptKey,
        string Grain,
        string FilterHash);

    private sealed record CreateRequestBindingV2(
        string Schema,
        string WorkId,
        string ScopeAssignmentId,
        string CommandId,
        string P9ResultKind,
        string P9ResultId,
        string P9RunId,
        string ConceptKey,
        string Grain,
        string FilterHash,
        string CanonicalFilterJson,
        ActualCapturePlanRequestBinding ActualCapturePlan,
        string ActualCapturePlanSha256,
        string ActualConfigurationBundleSha256);

    private sealed record ActualCapturePlanRequestBinding(
        string BoundaryRegistryVersion,
        string BasicSnapshotId,
        string BasicMode,
        string AdvancedSectionId,
        IReadOnlyList<string> AdvancedDayNodeIds,
        IReadOnlyList<string> AdvancedMonthNodeIds,
        IReadOnlyList<string> AdvancedYearNodeIds,
        string DiffResultId,
        string DiffRunId,
        string ApiSurface,
        string ApiOwnerResultId,
        string ExportId);

    private sealed record CreateRequestBindingV3(
        string Schema,
        string WorkId,
        string ScopeAssignmentId,
        string CommandId,
        string P9ResultKind,
        string P9ResultId,
        string P9RunId,
        string ConceptKey,
        string Grain,
        string FilterHash,
        string CanonicalFilterJson,
        ActualCapturePlanRequestBindingV4 ActualCapturePlan,
        string ActualCapturePlanSha256,
        string ActualConfigurationBundleSha256);

    private sealed record ActualCapturePlanRequestBindingV4(
        string BoundaryRegistryVersion,
        string BasicSnapshotId,
        string BasicMode,
        string AdvancedSectionId,
        IReadOnlyList<string> AdvancedDayNodeIds,
        IReadOnlyList<string> AdvancedMonthNodeIds,
        IReadOnlyList<string> AdvancedYearNodeIds,
        string DiffResultId,
        string DiffRunId,
        string ApiSurface,
        string ApiOwnerResultId,
        string ExportId,
        string BasicDisposition,
        string AdvancedDisposition,
        string DiffDisposition);

    private sealed record ReconciliationState(
        string Status,
        long StateRevision,
        int RetryCount,
        DateTime? NextRetryAtUtc,
        string? LeaseOwnerId,
        string? ClaimToken,
        DateTime? LeaseUntilUtc,
        DateTime? LastHeartbeatAtUtc,
        DateTime DeadlineAtUtc,
        string? DiagnosticCode,
        string? PendingGenerationId,
        string? PendingGenerationHash,
        DateTime? PendingGenerationPublishedAtUtc,
        string? CurrentGenerationId,
        string? CurrentGenerationHash,
        StatisticReconciliationRecheckMarker? Recheck,
        string? CurrentGenerationRecheckCaptureBindingSha256,
        string? CurrentRecheckFinalizeReceiptSha256,
        long ReviewDecisionRevision,
        long GenerationPublishRevision,
        string OperationReceiptHistoryHash,
        string? RecheckBeginReceiptHistoryHash,
        DateTime? CancelledAtUtc,
        DateTime? FailedAtUtc);
}
