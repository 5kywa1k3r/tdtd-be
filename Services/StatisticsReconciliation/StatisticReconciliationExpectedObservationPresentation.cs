using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

namespace tdtd_be.Services.StatisticsReconciliation;

internal sealed record StatisticReconciliationExpectedCursorScope(
    string ActorUserId,
    string WorkId,
    string ScopeAssignmentId,
    string ReconciliationId,
    string GenerationId,
    string ManifestSha256,
    string View,
    string PermissionCode);

internal sealed record StatisticReconciliationExpectedCursorPosition(
    string RecordKind,
    string RecordId);

internal sealed class StatisticReconciliationExpectedObservationCursorCodec
{
    internal const string SchemaVersion = "P10_EXPECTED_PROVENANCE_CURSOR_V1";
    internal const int MaxCursorLength = 4096;

    private static readonly JsonSerializerOptions CursorJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly byte[] _signingKey;

    internal StatisticReconciliationExpectedObservationCursorCodec(byte[] signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.Length < 32)
            throw new ArgumentException("Cursor signing key must contain at least 256 bits.",
                nameof(signingKey));
        _signingKey = signingKey.ToArray();
    }

    internal string Encode(
        StatisticReconciliationExpectedCursorScope scope,
        StatisticReconciliationExpectedCursorPosition position)
    {
        ValidateScope(scope);
        ValidatePosition(position);
        var envelope = new CursorEnvelope(
            SchemaVersion,
            scope.ActorUserId,
            scope.WorkId,
            scope.ScopeAssignmentId,
            scope.ReconciliationId,
            scope.GenerationId,
            scope.ManifestSha256,
            scope.View,
            scope.PermissionCode,
            position.RecordKind,
            position.RecordId);
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(envelope, CursorJson));
        var signature = Base64Url(HMACSHA256.HashData(
            _signingKey,
            Encoding.UTF8.GetBytes(payload)));
        return $"{payload}.{signature}";
    }

    internal StatisticReconciliationExpectedCursorPosition Decode(
        string value,
        StatisticReconciliationExpectedCursorScope expectedScope)
    {
        ValidateScope(expectedScope);
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxCursorLength ||
            value.Any(char.IsControl))
            throw InvalidCursor();
        var separator = value.LastIndexOf('.');
        if (separator <= 0 || separator == value.Length - 1)
            throw InvalidCursor();
        var payload = value[..separator];
        var suppliedSignatureText = value[(separator + 1)..];
        byte[] suppliedSignature;
        byte[] payloadBytes;
        try
        {
            suppliedSignature = DecodeBase64Url(suppliedSignatureText);
            payloadBytes = DecodeBase64Url(payload);
        }
        catch (FormatException)
        {
            throw InvalidCursor();
        }
        if (!string.Equals(
                suppliedSignatureText,
                Base64Url(suppliedSignature),
                StringComparison.Ordinal))
            throw InvalidCursor();
        var expectedSignature = HMACSHA256.HashData(
            _signingKey,
            Encoding.UTF8.GetBytes(payload));
        if (suppliedSignature.Length != expectedSignature.Length ||
            !CryptographicOperations.FixedTimeEquals(
                suppliedSignature,
                expectedSignature))
            throw InvalidCursor();

        CursorEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<CursorEnvelope>(payloadBytes, CursorJson)
                       ?? throw InvalidCursor();
        }
        catch (JsonException)
        {
            throw InvalidCursor();
        }
        var canonicalPayload = Base64Url(
            JsonSerializer.SerializeToUtf8Bytes(envelope, CursorJson));
        if (!string.Equals(payload, canonicalPayload, StringComparison.Ordinal))
            throw InvalidCursor();
        if (envelope.SchemaVersion != SchemaVersion ||
            envelope.ActorUserId != expectedScope.ActorUserId ||
            envelope.WorkId != expectedScope.WorkId ||
            envelope.ScopeAssignmentId != expectedScope.ScopeAssignmentId ||
            envelope.ReconciliationId != expectedScope.ReconciliationId ||
            envelope.GenerationId != expectedScope.GenerationId ||
            envelope.ManifestSha256 != expectedScope.ManifestSha256 ||
            envelope.View != expectedScope.View ||
            envelope.PermissionCode != expectedScope.PermissionCode)
            throw InvalidCursor();
        var position = new StatisticReconciliationExpectedCursorPosition(
            envelope.RecordKind,
            envelope.RecordId);
        ValidatePosition(position);
        return position;
    }

    private static void ValidateScope(StatisticReconciliationExpectedCursorScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        foreach (var value in new[]
                 {
                     scope.ActorUserId,
                     scope.WorkId,
                     scope.ScopeAssignmentId,
                     scope.ReconciliationId,
                     scope.GenerationId,
                     scope.ManifestSha256,
                     scope.View,
                     scope.PermissionCode
                 })
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
                value.Any(char.IsControl))
                throw InvalidCursor();
        }
        if (!StatisticReconciliationCanonicalJson.IsCanonicalSha256(scope.GenerationId) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(scope.ManifestSha256))
            throw InvalidCursor();
    }

    private static void ValidatePosition(
        StatisticReconciliationExpectedCursorPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        _ = StatisticReconciliationExpectedObservationIntegrity.RecordKindRank(
            position.RecordKind);
        if (!StatisticReconciliationCanonicalJson.IsCanonicalSha256(position.RecordId))
            throw InvalidCursor();
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        if (value.Length == 0 || value.Any(character =>
                character is not (>= 'A' and <= 'Z') and
                    not (>= 'a' and <= 'z') and
                    not (>= '0' and <= '9') and
                    not '-' and not '_'))
            throw new FormatException("Invalid base64url alphabet.");
        value = value.Replace('-', '+').Replace('_', '/');
        value = (value.Length % 4) switch
        {
            0 => value,
            2 => value + "==",
            3 => value + "=",
            _ => throw new FormatException("Invalid base64url length.")
        };
        return Convert.FromBase64String(value);
    }

    private static StatisticReconciliationExpectedCursorException InvalidCursor()
        => new();

    private sealed record CursorEnvelope(
        string SchemaVersion,
        string ActorUserId,
        string WorkId,
        string ScopeAssignmentId,
        string ReconciliationId,
        string GenerationId,
        string ManifestSha256,
        string View,
        string PermissionCode,
        string RecordKind,
        string RecordId);
}

internal sealed class StatisticReconciliationExpectedCursorException
    : Exception;

internal static class StatisticReconciliationExpectedObservationPresentation
{
    internal const string BusinessRedactionPolicy = "P10_BUSINESS_SUMMARY_REDACTION_V1";
    internal const string BusinessOrdering =
        "FAMILY_KIND_ATOM_TYPE_STATE_ORDINAL_ASC";
    internal const string ProvenanceOrdering =
        "RECORD_KIND_ORDINAL_ASC_RECORD_ID_ORDINAL_ASC";
    internal const string ProvenanceView = "OPERATOR_PROVENANCE_V1";

    internal static StatisticReconciliationExpectedBusinessSummaryResponse BusinessSummary(
        string reconciliationId,
        string generationId,
        string actorUserId,
        bool detailedProvenanceAllowed,
        string summaryPermissionCode,
        IReadOnlyList<StatisticReconciliationObservation> documents)
        => BusinessSummary(
            reconciliationId,
            generationId,
            actorUserId,
            detailedProvenanceAllowed,
            summaryPermissionCode,
            documents
                .Where(item =>
                    item.RecordKind ==
                    StatisticReconciliationObservationRecordKinds.ExpectedAtom)
                .Select(item => item.Atom ?? throw new InvalidOperationException(
                    "Expected atom payload is missing."))
                .ToArray());

    internal static StatisticReconciliationExpectedBusinessSummaryResponse BusinessSummary(
        string reconciliationId,
        string generationId,
        string actorUserId,
        bool detailedProvenanceAllowed,
        string summaryPermissionCode,
        IReadOnlyList<StatisticReconciliationObservationAtom> atoms)
    {
        var builder = CreateBusinessSummaryBuilder(
            reconciliationId,
            generationId,
            actorUserId,
            detailedProvenanceAllowed,
            summaryPermissionCode);
        foreach (var atom in atoms)
            builder.Add(atom);
        return builder.Build();
    }

    internal static StatisticReconciliationExpectedBusinessSummaryResponse BusinessSummary(
        string reconciliationId,
        string generationId,
        string actorUserId,
        bool detailedProvenanceAllowed,
        string summaryPermissionCode,
        IReadOnlyList<StatisticReconciliationExpectedBusinessBucket> buckets)
    {
        var builder = CreateBusinessSummaryBuilder(
            reconciliationId,
            generationId,
            actorUserId,
            detailedProvenanceAllowed,
            summaryPermissionCode);
        foreach (var bucket in buckets)
            builder.Add(bucket);
        return builder.Build();
    }

    internal static BusinessSummaryBuilder CreateBusinessSummaryBuilder(
        string reconciliationId,
        string generationId,
        string actorUserId,
        bool detailedProvenanceAllowed,
        string summaryPermissionCode)
        => new(
            reconciliationId,
            generationId,
            actorUserId,
            detailedProvenanceAllowed,
            summaryPermissionCode);
    internal static StatisticReconciliationExpectedProvenancePageResponse ProvenancePage(
        string reconciliationId,
        string generationId,
        string manifestSha256,
        string actorUserId,
        string detailPermissionCode,
        IReadOnlyList<StatisticReconciliationObservation> documents,
        int pageSize,
        StatisticReconciliationExpectedCursorPosition? after,
        Func<StatisticReconciliationExpectedCursorPosition, string> encodeCursor)
    {
        var ordered = documents
            .OrderBy(item => item.RecordKind, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Where(item => after is null || ComparePosition(
                item.RecordKind,
                item.Id,
                after.RecordKind,
                after.RecordId) > 0)
            .Take(pageSize + 1)
            .ToArray();
        return ProvenancePageFromOrderedRows(
            reconciliationId,
            generationId,
            manifestSha256,
            actorUserId,
            detailPermissionCode,
            ordered,
            documents.Count,
            pageSize,
            encodeCursor);
    }

    internal static StatisticReconciliationExpectedProvenancePageResponse
        ProvenancePageFromOrderedRows(
            string reconciliationId,
            string generationId,
            string manifestSha256,
            string actorUserId,
            string detailPermissionCode,
            IReadOnlyList<StatisticReconciliationObservation> orderedRows,
            long total,
            int pageSize,
            Func<StatisticReconciliationExpectedCursorPosition, string> encodeCursor)
    {
        if (pageSize is < 1 or >
            StatisticReconciliationRunService.ExpectedObservationMaxPageSize ||
            orderedRows.Count > pageSize + 1 || total < 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        for (var index = 0; index < orderedRows.Count; index++)
        {
            _ = StatisticReconciliationExpectedObservationIntegrity.RecordKindRank(
                orderedRows[index].RecordKind);
            if (index > 0 && ComparePosition(
                    orderedRows[index - 1].RecordKind,
                    orderedRows[index - 1].Id,
                    orderedRows[index].RecordKind,
                    orderedRows[index].Id) >= 0)
                throw new InvalidOperationException(
                    "Provenance page is not in canonical keyset order.");
        }
        var hasMore = orderedRows.Count > pageSize;
        var page = orderedRows.Take(pageSize).ToArray();
        var rows = page.Select(ToProvenanceRow).ToArray();
        var nextCursor = hasMore && page.Length > 0
            ? encodeCursor(new StatisticReconciliationExpectedCursorPosition(
                page[^1].RecordKind,
                page[^1].Id))
            : null;
        return new StatisticReconciliationExpectedProvenancePageResponse(
            reconciliationId,
            generationId,
            manifestSha256,
            ProvenanceOrdering,
            new StatisticReconciliationExpectedPermissionSnapshotResponse(
                actorUserId,
                detailPermissionCode,
                true,
                total,
                total),
            rows,
            total,
            pageSize,
            nextCursor);
    }

    private static int ComparePosition(
        string leftKind,
        string leftId,
        string rightKind,
        string rightId)
    {
        var kind = string.CompareOrdinal(leftKind, rightKind);
        return kind != 0 ? kind : string.CompareOrdinal(leftId, rightId);
    }
    internal static StatisticReconciliationExpectedProvenanceRowResponse ToProvenanceRow(
        StatisticReconciliationObservation document)
        => new(
            document.Id,
            document.RecordKind,
            Header(document),
            Source(document.SourceDecision),
            Configuration(document.ConfigurationPin),
            Lineage(document.LineagePin),
            Atom(document.Atom),
            Commit(document.Commit),
            document.DocumentSemanticSha256);

    private static StatisticReconciliationExpectedObservationHeaderResponse Header(
        StatisticReconciliationObservation value)
        => new(
            value.SchemaVersion,
            value.ReconciliationId,
            value.ImmutableIdentitySha256,
            value.ImmutableHeaderSha256,
            value.GenerationId,
            value.GenerationSemanticSha256,
            value.AlgorithmRevision,
            value.AlgorithmSha256,
            value.SourceSetSha256,
            value.TypedSemanticSha256,
            value.MetricPlanSha256,
            value.InputBindingSha256,
            value.LifecycleSemanticSha256,
            value.ContributionSemanticSha256,
            value.TenantUnitId,
            value.WorkId,
            value.ScopeAssignmentId,
            value.PeriodKey,
            value.PeriodInstanceKey,
            value.ConceptKey,
            value.Grain,
            value.TimeAxis,
            value.FilterSha256,
            value.DynamicFormVersionId,
            value.DynamicFormSchemaSha256,
            value.FlowTemplateVersionId,
            value.FlowPayloadSha256,
            value.FlowInstanceId,
            value.ExecutionEpochId,
            value.ExecutionEpoch ?? 0,
            value.ExecutionEpochRevision ?? 0,
            value.P8ConfigurationOwnerId,
            value.P8ConfigurationBundleSha256,
            Catalog(value.CatalogPins));

    private static StatisticReconciliationExpectedCatalogPinsResponse Catalog(
        StatisticReconciliationObservationCatalogPins value)
        => new(
            value.P9CatalogVersion,
            value.P9CatalogRawSha256,
            value.P9CatalogSemanticSha256,
            value.P9SchemaRawSha256,
            value.P9SchemaSemanticSha256,
            value.P9StageLockSha256,
            value.CandidateChainId,
            value.CandidatePromptId,
            value.CandidateCatalogVersion,
            value.CandidateCatalogRawSha256,
            value.CandidateCatalogSemanticSha256,
            value.CandidateSchemaRawSha256,
            value.CandidateSchemaSemanticSha256,
            value.CandidateStageLockSha256,
            value.CatalogPinSetSha256);

    private static StatisticReconciliationExpectedSourceDecisionResponse? Source(
        StatisticReconciliationObservationSourceDecision? value)
        => value is null ? null : new(
            value.StableSourceId,
            value.IdentityKey,
            value.ReportId,
            value.PayloadDocumentId,
            value.PayloadRevision,
            value.PayloadOwnerSha256,
            value.PayloadCanonicalSha256,
            value.LifecycleRevision,
            value.LifecycleSha256,
            value.LifecycleStatus,
            value.IsEffective,
            value.IsLocked,
            value.RuntimeDisposition,
            value.Disposition,
            value.ReasonCode,
            value.ContributionPolicy,
            value.ContributionVersionId,
            value.ContributionRevision,
            value.ContributionPolicySha256,
            value.ContributionProvenanceId,
            value.ContributionProvenanceSha256,
            value.DecisionSemanticSha256);

    private static StatisticReconciliationExpectedConfigurationPinResponse? Configuration(
        StatisticReconciliationObservationConfigurationPin? value)
        => value is null ? null : new(
            value.Kind,
            value.OwnerId,
            value.ConfigId,
            value.VersionId,
            value.VersionNo,
            value.Revision,
            value.ConfigSha256);

    private static StatisticReconciliationExpectedLineagePinResponse? Lineage(
        StatisticReconciliationObservationLineagePin? value)
        => value is null ? null : new(
            value.Layer,
            value.OwnerId,
            value.VersionId,
            value.Revision,
            value.Sha256);

    private static StatisticReconciliationExpectedAtomResponse? Atom(
        StatisticReconciliationObservationAtom? value)
        => value is null ? null : new(
            value.IdentitySha256,
            value.Family,
            value.Kind,
            value.MetricId,
            value.FieldId,
            value.TableId,
            value.RowId,
            value.LabelId,
            value.BasicScope,
            value.BasicScopeId,
            value.FlowBranchId,
            value.FlowStepId,
            value.AdvancedGrain,
            value.DiffKind,
            value.PeriodKey,
            value.AtomKind,
            value.ValueType,
            value.ValueState,
            value.CanonicalValue,
            value.DecimalScale,
            value.OccurrenceCount,
            value.ReportCount,
            value.RowCount,
            value.NumericValueCount,
            value.ValueIdentitySha256,
            value.AtomSemanticSha256);

    private static StatisticReconciliationExpectedCommitResponse? Commit(
        StatisticReconciliationObservationCommit? value)
        => value is null ? null : new(
            value.SourceDecisionCount,
            value.ConfigurationPinCount,
            value.LineagePinCount,
            value.AtomCount,
            value.DocumentCount,
            value.ManifestSha256);

    internal sealed class BusinessSummaryBuilder(
        string reconciliationId,
        string generationId,
        string actorUserId,
        bool detailedProvenanceAllowed,
        string summaryPermissionCode)
    {
        private readonly Dictionary<BusinessKey, long> _aggregates = [];
        private long _atomCount;

        internal void Add(StatisticReconciliationObservationAtom atom)
        {
            ArgumentNullException.ThrowIfNull(atom);
            Add(
                new BusinessKey(
                    atom.Family,
                    atom.Kind,
                    atom.AtomKind,
                    atom.ValueType,
                    atom.ValueState),
                1L);
        }

        internal void Add(StatisticReconciliationExpectedBusinessBucket bucket)
        {
            ArgumentNullException.ThrowIfNull(bucket);
            Add(
                new BusinessKey(
                    bucket.Family,
                    bucket.Kind,
                    bucket.AtomKind,
                    bucket.ValueType,
                    bucket.ValueState),
                bucket.AtomCount);
        }

        private void Add(BusinessKey key, long atomCount)
        {
            if (atomCount < 0)
                throw new ArgumentOutOfRangeException(nameof(atomCount));
            _aggregates[key] = checked(
                _aggregates.GetValueOrDefault(key) + atomCount);
            _atomCount = checked(_atomCount + atomCount);
        }

        internal StatisticReconciliationExpectedBusinessSummaryResponse Build()
        {
            var rows = _aggregates
                .OrderBy(item => item.Key.Family, StringComparer.Ordinal)
                .ThenBy(item => item.Key.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.Key.AtomKind, StringComparer.Ordinal)
                .ThenBy(item => item.Key.ValueType, StringComparer.Ordinal)
                .ThenBy(item => item.Key.ValueState, StringComparer.Ordinal)
                .Select(item =>
                    new StatisticReconciliationExpectedBusinessMetricRowResponse(
                        item.Key.Family,
                        item.Key.Kind,
                        item.Key.AtomKind,
                        item.Key.ValueType,
                        item.Key.ValueState,
                        item.Value))
                .ToArray();
            return new StatisticReconciliationExpectedBusinessSummaryResponse(
                reconciliationId,
                generationId,
                "COMMITTED",
                BusinessOrdering,
                BusinessRedactionPolicy,
                new StatisticReconciliationExpectedPermissionSnapshotResponse(
                    actorUserId,
                    summaryPermissionCode,
                    detailedProvenanceAllowed,
                    _atomCount,
                    rows.LongLength),
                rows,
                rows.LongLength);
        }
    }

    private sealed record BusinessKey(
        string Family,
        string Kind,
        string AtomKind,
        string ValueType,
        string ValueState);
}