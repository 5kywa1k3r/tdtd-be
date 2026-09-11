using System.Collections.Immutable;
using System.Text.Json;
using tdtd_be.Models;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed class StatisticReconciliationActualBasicAdapter
{
    internal const int MaxSourceIds = 10_000;
    internal const int MaxResultItems = 100_000;
    private const int CompactPayloadVersion = 9;
    private const string CandidatePrompt = "P9-04";
    private const int CandidateStage = 2;
    private const string CandidateCatalogRaw =
        "7955d4c0fb1aa03b5d28484b15f321752b16983996f3b5c5428d9192ff67a509";
    private const string CandidateCatalogSemantic =
        "c7120bd77d338006e8df117c83718ee694aa407167a7ca214e2f71dc2f2da9f1";
    private const string CandidateStageLock =
        "38d13a94dfe863625ae54396ceee6beccce9bf9de85cd4d26cb0e2e730fdf1ad";

    internal Task<ActualBasicResultObservation>
        CaptureDirectChildrenOrSelfAsync(
            ActualBasicOwnerBoundary boundary,
            IStatisticReconciliationActualBasicOwnerReader reader,
            CancellationToken cancellationToken = default)
        => CaptureAsync(
            StatisticReconciliationActualBasicModes.DirectChildrenOrSelf,
            boundary, reader, cancellationToken);

    internal Task<ActualBasicResultObservation> CaptureDirectChildrenAsync(
        ActualBasicOwnerBoundary boundary,
        IStatisticReconciliationActualBasicOwnerReader reader,
        CancellationToken cancellationToken = default)
        => CaptureAsync(
            StatisticReconciliationActualBasicModes.DirectChildren,
            boundary, reader, cancellationToken);

    internal Task<ActualBasicResultObservation> CaptureFlowBranchAsync(
        ActualBasicOwnerBoundary boundary,
        IStatisticReconciliationActualBasicOwnerReader reader,
        CancellationToken cancellationToken = default)
        => CaptureAsync(StatisticReconciliationActualBasicModes.FlowBranch,
            boundary, reader, cancellationToken);

    internal Task<ActualBasicResultObservation> CaptureFlowStepAsync(
        ActualBasicOwnerBoundary boundary,
        IStatisticReconciliationActualBasicOwnerReader reader,
        CancellationToken cancellationToken = default)
        => CaptureAsync(StatisticReconciliationActualBasicModes.FlowStep,
            boundary, reader, cancellationToken);

    internal Task<ActualBasicResultObservation> CaptureFlowEffectivePathAsync(
        ActualBasicOwnerBoundary boundary,
        IStatisticReconciliationActualBasicOwnerReader reader,
        CancellationToken cancellationToken = default)
        => CaptureAsync(StatisticReconciliationActualBasicModes.FlowEffectivePath,
            boundary, reader, cancellationToken);

    internal Task<ActualBasicResultObservation> CaptureFlowFinalAsync(
        ActualBasicOwnerBoundary boundary,
        IStatisticReconciliationActualBasicOwnerReader reader,
        CancellationToken cancellationToken = default)
        => CaptureAsync(StatisticReconciliationActualBasicModes.FlowFinal,
            boundary, reader, cancellationToken);

    private static async Task<ActualBasicResultObservation> CaptureAsync(
        string requiredMode,
        ActualBasicOwnerBoundary boundary,
        IStatisticReconciliationActualBasicOwnerReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(reader);
        var normalized = NormalizeBoundary(boundary, requiredMode);
        var owner = await reader.ReadSnapshotAsync(normalized, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Fail("BASIC_OWNER_SNAPSHOT_NOT_FOUND");
        RequireRootBoundary(normalized, owner);

        var assignments = SnapshotIds(owner.SourceAssignmentIds,
            "BASIC_SOURCE_ASSIGNMENT_ID");
        var reports = SnapshotIds(owner.SourceReportIds,
            "BASIC_SOURCE_REPORT_ID");
        using var requestDocument = StatisticReconciliationActualJson.ParseStrict(
            owner.RequestJson, "BASIC_REQUEST_JSON");
        using var snapshotDocument = StatisticReconciliationActualJson.ParseStrict(
            owner.SnapshotJson, "BASIC_SNAPSHOT_JSON");
        var payload = ReadPayload(snapshotDocument.RootElement, owner);
        if (payload.Items.Length > MaxResultItems)
            throw Fail("BASIC_RESULT_ITEMS_LIMIT");

        var refreshStatus = ExactUpper(
            owner.RefreshStatus, "BASIC_REFRESH_STATUS");
        var lifecycle = LifecycleSemantic(owner, refreshStatus, assignments, reports);
        var innerMatches = InnerMetaMatches(payload.Meta, owner);
        var state = new ActualBasicOwnerState(
            refreshStatus,
            owner.IsDeleted,
            owner.SnapshotDirty,
            owner.SnapshotRefreshedAtUtc.HasValue,
            !owner.IsDeleted && !owner.SnapshotDirty
                && refreshStatus == WorkAssignmentBasicSummaryRefreshStatuses.Done
                && owner.SnapshotRefreshedAtUtc?.Kind == DateTimeKind.Utc,
            false,
            innerMatches,
            // Audit only: production preserves path/assignee ordering, which
            // need not be lexicographic by persisted identifier.
            IsCanonicalSequence(assignments),
            IsCanonicalSequence(reports),
            assignments.Distinct(StringComparer.Ordinal).Count() == assignments.Length
                && reports.Distinct(StringComparer.Ordinal).Count() == reports.Length,
            lifecycle);

        var requestRawSha = StatisticReconciliationActualJson.RawSha256(owner.RequestJson);
        var requestCanonicalSha = StatisticReconciliationActualJson.CanonicalSha256(
            requestDocument.RootElement);
        var snapshotRawSha = StatisticReconciliationActualJson.RawSha256(owner.SnapshotJson);
        var snapshotCanonicalSha = StatisticReconciliationActualJson.CanonicalSha256(
            snapshotDocument.RootElement);
        var assignmentSetSha = SetSha("P10_ACTUAL_BASIC_ASSIGNMENT_SET_V1", assignments);
        var reportSetSha = SetSha("P10_ACTUAL_BASIC_REPORT_SET_V1", reports);
        var captureSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_BASIC_RESULT_CAPTURE_V1",
            BoundarySemantic(normalized),
            requestRawSha,
            requestCanonicalSha,
            snapshotRawSha,
            snapshotCanonicalSha,
            assignmentSetSha,
            reportSetSha,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_BASIC_ASSIGNMENT_ORDER_V1", assignments),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_BASIC_REPORT_ORDER_V1", reports),
            StatisticReconciliationActualCanonical.Sha256(
                owner.SourceSignatureHash, "BASIC_SOURCE_SIGNATURE"),
            lifecycle,
            "FLOW_RUNTIME_REVISION_NOT_PERSISTED_BY_BASIC_OWNER",
            StatisticReconciliationActualCanonical.Boolean(innerMatches),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_BASIC_RESULT_ITEMS_V1",
                payload.Items.Select(x => x.SemanticSha256)));

        return new ActualBasicResultObservation(
            normalized,
            owner.Id,
            owner.RequestJson,
            requestRawSha,
            requestCanonicalSha,
            payload.Kind,
            payload.Version,
            owner.SnapshotJson,
            snapshotRawSha,
            snapshotCanonicalSha,
            assignments,
            reports,
            assignmentSetSha,
            reportSetSha,
            StatisticReconciliationActualCanonical.Sha256(
                owner.SourceSignatureHash, "BASIC_SOURCE_SIGNATURE"),
            payload.Items,
            state,
            captureSha);
    }

    private static ActualBasicOwnerBoundary NormalizeBoundary(
        ActualBasicOwnerBoundary value,
        string requiredMode)
    {
        if (value.ConfigVersionNo < 1 || value.ConfigRevision < 1)
            throw Fail("BASIC_BOUNDARY_REVISION_INVALID");
        if (value.CandidateStage != CandidateStage
            || !StringComparer.Ordinal.Equals(value.CandidatePromptId, CandidatePrompt)
            || !StringComparer.Ordinal.Equals(value.CandidateCatalogRawSha256, CandidateCatalogRaw)
            || !StringComparer.Ordinal.Equals(value.CandidateCatalogSemanticSha256, CandidateCatalogSemantic)
            || !StringComparer.Ordinal.Equals(value.CandidateStageLockSha256, CandidateStageLock))
            throw Fail("BASIC_FROZEN_CANDIDATE_MISMATCH");
        var mode = ExactUpper(
            value.SourceScopeMode, "BASIC_SOURCE_SCOPE_MODE");
        if (mode != requiredMode
            || !StatisticReconciliationActualBasicModes.ExactSet.Contains(
                mode, StringComparer.Ordinal))
            throw Fail("BASIC_SOURCE_SCOPE_MODE_MISMATCH");
        var flowMode =
            StatisticReconciliationActualBasicModes.IsFlow(mode);
        var instance = flowMode
            ? StatisticReconciliationActualCanonical.Required(
                value.SourceFlowInstanceId, "BASIC_FLOW_INSTANCE_ID")
            : StatisticReconciliationActualCanonical.Optional(
                value.SourceFlowInstanceId, "BASIC_FLOW_INSTANCE_ID");
        var step = StatisticReconciliationActualCanonical.Optional(
            value.SourceFlowStepId, "BASIC_FLOW_STEP_ID");
        var branch = StatisticReconciliationActualCanonical.Optional(
            value.SourceFlowBranchId, "BASIC_FLOW_BRANCH_ID");
        if (!flowMode)
        {
            if (instance is not null || step is not null || branch is not null ||
                value.SourceFlowEffectiveStatus is not null)
                throw Fail("BASIC_NON_FLOW_SCOPE_INVALID");
        }
        else if (mode == StatisticReconciliationActualBasicModes.FlowStep)
        {
            if (step is null || branch is not null)
                throw Fail("BASIC_FLOW_STEP_SCOPE_INVALID");
        }
        else if (mode == StatisticReconciliationActualBasicModes.FlowBranch)
        {
            if (branch is null || step is not null)
                throw Fail("BASIC_FLOW_BRANCH_SCOPE_INVALID");
        }
        else if (step is not null || branch is not null)
            throw Fail("BASIC_FLOW_PATH_SCOPE_INVALID");

        return value with
        {
            OwnerSnapshotId = StatisticReconciliationActualCanonical.Required(
                value.OwnerSnapshotId, "BASIC_OWNER_SNAPSHOT_ID"),
            WorkId = StatisticReconciliationActualCanonical.Required(
                value.WorkId, "BASIC_WORK_ID"),
            ScopeAssignmentId = StatisticReconciliationActualCanonical.Required(
                value.ScopeAssignmentId, "BASIC_SCOPE_ASSIGNMENT_ID"),
            DynamicFormTemplateId = StatisticReconciliationActualCanonical.Required(
                value.DynamicFormTemplateId, "BASIC_FORM_TEMPLATE_ID"),
            SourceScopeMode = mode,
            SourceFlowInstanceId = instance,
            SourceFlowStepId = step,
            SourceFlowBranchId = branch,
            SourceFlowEffectiveStatus = flowMode
                ? StatisticReconciliationActualCanonical.Optional(
                    value.SourceFlowEffectiveStatus,
                    "BASIC_FLOW_EFFECTIVE_STATUS")
                : null,
            RequestHash = StatisticReconciliationActualCanonical.Sha256(
                value.RequestHash, "BASIC_REQUEST_HASH"),
            ConfigId = StatisticReconciliationActualCanonical.Required(
                value.ConfigId, "BASIC_CONFIG_ID"),
            ConfigVersionId = StatisticReconciliationActualCanonical.Required(
                value.ConfigVersionId, "BASIC_CONFIG_VERSION_ID"),
            ConfigSha256 = StatisticReconciliationActualCanonical.Sha256(
                value.ConfigSha256, "BASIC_CONFIG_SHA256"),
            ConfigDependencyPins = SnapshotBoundaryPins(
                value.ConfigDependencyPins, "BASIC_CONFIG_DEPENDENCY_PIN"),
            CandidateChainId = StatisticReconciliationActualCanonical.Required(
                value.CandidateChainId, "BASIC_CANDIDATE_CHAIN_ID"),
            CandidatePromptId = CandidatePrompt,
            CandidateStage = CandidateStage,
            CandidateCatalogRawSha256 = CandidateCatalogRaw,
            CandidateCatalogSemanticSha256 = CandidateCatalogSemantic,
            CandidateStageLockSha256 = CandidateStageLock
        };
    }

    private static void RequireRootBoundary(
        ActualBasicOwnerBoundary boundary,
        WorkAssignmentBasicSummarySnapshot owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!Equals(owner.Id, boundary.OwnerSnapshotId)
            || !Equals(owner.WorkId, boundary.WorkId)
            || !Equals(owner.ScopeAssignmentId, boundary.ScopeAssignmentId)
            || !Equals(owner.DynamicFormTemplateId, boundary.DynamicFormTemplateId)
            || !Equals(owner.SourceScopeMode, boundary.SourceScopeMode)
            || !Equals(owner.SourceFlowInstanceId, boundary.SourceFlowInstanceId)
            || !Equals(owner.SourceFlowStepId, boundary.SourceFlowStepId)
            || !Equals(owner.SourceFlowBranchId, boundary.SourceFlowBranchId)
            || !Equals(owner.SourceFlowEffectiveStatus, boundary.SourceFlowEffectiveStatus)
            || !Equals(owner.RequestHash, boundary.RequestHash)
            || !Equals(owner.ConfigId, boundary.ConfigId)
            || !Equals(owner.ConfigVersionId, boundary.ConfigVersionId)
            || owner.ConfigVersionNo != boundary.ConfigVersionNo
            || owner.ConfigRevision != boundary.ConfigRevision
            || !Equals(owner.ConfigHash, boundary.ConfigSha256)
            || !Sequence(owner.ConfigDependencyPins, boundary.ConfigDependencyPins)
            || !Equals(owner.CandidateChainId, boundary.CandidateChainId)
            || !Equals(owner.CandidatePromptId, boundary.CandidatePromptId)
            || owner.CandidateStage != boundary.CandidateStage
            || !Equals(owner.CandidateCatalogRawSha256, boundary.CandidateCatalogRawSha256)
            || !Equals(owner.CandidateCatalogSemanticSha256, boundary.CandidateCatalogSemanticSha256)
            || !Equals(owner.CandidateStageLockSha256, boundary.CandidateStageLockSha256))
            throw Fail("BASIC_OWNER_BOUNDARY_MISMATCH");
    }

    private static PayloadRead ReadPayload(
        JsonElement root,
        WorkAssignmentBasicSummarySnapshot owner)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw Fail("BASIC_SNAPSHOT_ROOT_INVALID");
        var compact = root.TryGetProperty("v", out var versionElement);
        var version = compact && versionElement.TryGetInt32(out var parsed)
            ? parsed
            : 0;
        if (compact && version != CompactPayloadVersion)
            throw Fail("BASIC_SNAPSHOT_VERSION_UNSUPPORTED");
        var meta = RequiredObject(root, "meta", "BASIC_SNAPSHOT_META");
        var items = ImmutableArray.CreateBuilder<ActualBasicPayloadItemObservation>();
        AddItems(root, "fields", "FIELD", items);
        AddItems(root, compact ? "tb" : "tables", compact ? "TABLE_BLOCK" : "TABLE", items);
        if (items.Count > MaxResultItems)
            throw Fail("BASIC_RESULT_ITEMS_LIMIT");
        return new PayloadRead(
            compact ? "COMPACT_VALUES1D_V9" : "LEGACY_FULL_RESPONSE",
            version,
            meta.Clone(),
            items.ToImmutable());
    }

    private static void AddItems(
        JsonElement root,
        string property,
        string section,
        ImmutableArray<ActualBasicPayloadItemObservation>.Builder output)
    {
        if (!root.TryGetProperty(property, out var array)
            || array.ValueKind != JsonValueKind.Array)
            throw Fail($"BASIC_SNAPSHOT_{property.ToUpperInvariant()}_INVALID");
        var ordinal = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw Fail("BASIC_RESULT_ITEM_INVALID");
            var canonical = StatisticReconciliationActualJson.Canonicalize(item);
            var identity = FirstString(item,
                "fieldKey", "targetKey", "b", "blockId", "metricKey")
                ?? $"ordinal:{ordinal}";
            output.Add(new ActualBasicPayloadItemObservation(
                section,
                ordinal,
                identity,
                canonical,
                StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_BASIC_RESULT_ITEM_V1",
                    section,
                    StatisticReconciliationActualCanonical.Integer(ordinal),
                    identity,
                    StatisticReconciliationActualJson.RawSha256(canonical))));
            ordinal++;
        }
    }

    private static bool InnerMetaMatches(
        JsonElement meta,
        WorkAssignmentBasicSummarySnapshot owner)
        => String(meta, "snapshotId") == owner.Id
           && String(meta, "scopeAssignmentId") == owner.ScopeAssignmentId
           && String(meta, "dynamicFormTemplateId") == owner.DynamicFormTemplateId
           && String(meta, "sourceScopeMode") == owner.SourceScopeMode
           && String(meta, "sourceFlowInstanceId") == owner.SourceFlowInstanceId
           && String(meta, "sourceFlowStepId") == owner.SourceFlowStepId
           && String(meta, "sourceFlowBranchId") == owner.SourceFlowBranchId
           && String(meta, "sourceFlowEffectiveStatus") == owner.SourceFlowEffectiveStatus
           && String(meta, "sourceSignatureHash") == owner.SourceSignatureHash
           && String(meta, "configId") == owner.ConfigId
           && String(meta, "configVersionId") == owner.ConfigVersionId
           && Long(meta, "configVersionNo") == owner.ConfigVersionNo
           && Long(meta, "configRevision") == owner.ConfigRevision
           && String(meta, "configHash") == owner.ConfigHash
           && String(meta, "candidateChainId") == owner.CandidateChainId
           && String(meta, "candidatePromptId") == owner.CandidatePromptId
           && Long(meta, "candidateStage") == owner.CandidateStage
           && String(meta, "candidateCatalogRawSha256") == owner.CandidateCatalogRawSha256
           && String(meta, "candidateCatalogSemanticSha256") == owner.CandidateCatalogSemanticSha256
           && String(meta, "candidateStageLockSha256") == owner.CandidateStageLockSha256;

    private static string LifecycleSemantic(
        WorkAssignmentBasicSummarySnapshot owner,
        string refreshStatus,
        ImmutableArray<string> assignments,
        ImmutableArray<string> reports)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_BASIC_OWNER_LIFECYCLE_V1",
            refreshStatus,
            StatisticReconciliationActualCanonical.Boolean(owner.IsDeleted),
            StatisticReconciliationActualCanonical.Boolean(owner.SnapshotDirty),
            Instant(owner.SnapshotDirtyAtUtc),
            Instant(owner.SnapshotRefreshedAtUtc),
            owner.RefreshJobId,
            owner.RefreshCorrelationId,
            owner.RefreshRequestedByUserId,
            owner.RefreshResetByUserId,
            Instant(owner.RefreshQueuedAtUtc),
            Instant(owner.RefreshStartedAtUtc),
            Instant(owner.RefreshFinishedAtUtc),
            Instant(owner.RefreshResetAtUtc),
            owner.RefreshError,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_BASIC_OWNER_ASSIGNMENT_ORDER_V1", assignments),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_BASIC_OWNER_REPORT_ORDER_V1", reports),
            StatisticReconciliationActualCanonical.Instant(owner.CreatedAtUtc),
            StatisticReconciliationActualCanonical.Instant(owner.UpdatedAtUtc),
            owner.CreatedByUserId,
            owner.UpdatedByUserId);

    private static string BoundarySemantic(ActualBasicOwnerBoundary value)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_BASIC_BOUNDARY_V1",
            value.OwnerSnapshotId,
            value.WorkId,
            value.ScopeAssignmentId,
            value.DynamicFormTemplateId,
            value.SourceScopeMode,
            value.SourceFlowInstanceId,
            value.SourceFlowStepId,
            value.SourceFlowBranchId,
            value.SourceFlowEffectiveStatus,
            value.RequestHash,
            value.ConfigId,
            value.ConfigVersionId,
            StatisticReconciliationActualCanonical.Integer(value.ConfigVersionNo),
            StatisticReconciliationActualCanonical.Integer(value.ConfigRevision),
            value.ConfigSha256,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_BASIC_CONFIG_DEPENDENCIES_V1", value.ConfigDependencyPins),
            value.CandidateChainId,
            value.CandidatePromptId,
            StatisticReconciliationActualCanonical.Integer(value.CandidateStage),
            value.CandidateCatalogRawSha256,
            value.CandidateCatalogSemanticSha256,
            value.CandidateStageLockSha256);

    private static ImmutableArray<string> SnapshotIds(
        IEnumerable<string>? values,
        string name)
    {
        ArgumentNullException.ThrowIfNull(values);
        var output = values.Select(x =>
                StatisticReconciliationActualCanonical.Required(x, name))
            .ToImmutableArray();
        if (output.Length > MaxSourceIds)
            throw Fail("BASIC_SOURCE_IDS_LIMIT");
        return output;
    }

    private static ImmutableArray<string> SnapshotBoundaryPins(
        ImmutableArray<string> values,
        string name)
    {
        if (values.IsDefault || values.Length > MaxSourceIds)
            throw Fail("BASIC_DEPENDENCY_PINS_INVALID");
        var output = values.Select(x =>
                StatisticReconciliationActualCanonical.Required(x, name))
            .ToImmutableArray();
        if (output.Distinct(StringComparer.Ordinal).Count() != output.Length)
            throw Fail("BASIC_DEPENDENCY_PINS_DUPLICATE");
        return output;
    }

    private static string SetSha(string domain, ImmutableArray<string> values)
        => StatisticReconciliationActualCanonical.HashSequence(
            domain,
            values.Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal));

    private static bool IsCanonicalSequence(ImmutableArray<string> values)
        => values.SequenceEqual(values.OrderBy(x => x, StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static JsonElement RequiredObject(
        JsonElement root,
        string property,
        string reason)
    {
        if (!root.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Object)
            throw Fail($"{reason}_INVALID");
        return value;
    }

    private static string? FirstString(JsonElement value, params string[] properties)
    {
        foreach (var property in properties)
        {
            var found = String(value, property);
            if (!string.IsNullOrWhiteSpace(found))
                return found;
        }
        return null;
    }

    private static string? String(JsonElement value, string property)
        => value.TryGetProperty(property, out var found)
           && found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;

    private static long? Long(JsonElement value, string property)
        => value.TryGetProperty(property, out var found)
           && found.ValueKind == JsonValueKind.Number
           && found.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static string? Instant(DateTime? value)
        => value.HasValue
            ? StatisticReconciliationActualCanonical.Instant(
                value.Value.Kind == DateTimeKind.Utc
                    ? value.Value
                    : throw Fail("BASIC_OWNER_TIME_NOT_UTC"))
            : null;

    private static bool Equals(string? left, string? right)
        => StringComparer.Ordinal.Equals(left, right);

    private static bool Sequence(IEnumerable<string>? left, ImmutableArray<string> right)
        => left is not null && left.SequenceEqual(right, StringComparer.Ordinal);

    private static string ExactUpper(string? value, string name)
    {
        var required = StatisticReconciliationActualCanonical.Required(value, name);
        if (!StringComparer.Ordinal.Equals(required, required.ToUpperInvariant()))
            throw Fail($"{name}_NON_CANONICAL_CASE" );
        return required;
    }

    private static StatisticReconciliationActualObservationException Fail(string reason)
        => new(reason);

    private sealed record PayloadRead(
        string Kind,
        int Version,
        JsonElement Meta,
        ImmutableArray<ActualBasicPayloadItemObservation> Items);
}
