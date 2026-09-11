using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal sealed class StatisticReconciliationExpectedObservationIntegrityAccumulator
{
    private readonly List<string> _manifestSemanticSha256 = [];
    private readonly List<AtomHash> _atomHashes = [];
    private readonly Dictionary<BusinessBucketKey, long> _businessBuckets = [];

    internal IReadOnlyList<StatisticReconciliationExpectedBusinessBucket>
        BusinessBuckets { get; private set; } = [];
    private string? _previousDocumentId;
    private string? _headerFingerprint;
    private StatisticReconciliationObservation? _commit;
    private int _documentCount;
    private int _sourceDecisionCount;
    private int _configurationPinCount;
    private int _lineagePinCount;
    private int _atomCount;
    private bool _completed;

    internal void Add(StatisticReconciliationObservation document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_completed)
            throw Invalid("GENERATION_VALIDATION_ALREADY_COMPLETED");
        if (_documentCount >=
            StatisticReconciliationExpectedObservationIntegrity.MaxGenerationDocuments)
            throw Invalid("GENERATION_DOCUMENT_COUNT_OUT_OF_RANGE");
        if (_previousDocumentId is not null &&
            string.CompareOrdinal(_previousDocumentId, document.Id) >= 0)
            throw Invalid("DOCUMENT_ID_ORDER_OR_DUPLICATE_INVALID");

        StatisticReconciliationExpectedObservationIntegrity.ValidateDocument(document);
        var headerFingerprint =
            StatisticReconciliationExpectedObservationIntegrity.HeaderFingerprint(document);
        if (_headerFingerprint is null)
            _headerFingerprint = headerFingerprint;
        else if (!string.Equals(
                     _headerFingerprint,
                     headerFingerprint,
                     StringComparison.Ordinal))
            throw Invalid("COMMON_HEADER_MISMATCH");

        _documentCount++;
        _previousDocumentId = document.Id;
        switch (document.RecordKind)
        {
            case StatisticReconciliationObservationRecordKinds.SourceDecision:
                _sourceDecisionCount++;
                _manifestSemanticSha256.Add(document.DocumentSemanticSha256);
                break;
            case StatisticReconciliationObservationRecordKinds.ConfigurationPin:
                _configurationPinCount++;
                _manifestSemanticSha256.Add(document.DocumentSemanticSha256);
                break;
            case StatisticReconciliationObservationRecordKinds.LineagePin:
                _lineagePinCount++;
                _manifestSemanticSha256.Add(document.DocumentSemanticSha256);
                break;
            case StatisticReconciliationObservationRecordKinds.ExpectedAtom:
                _atomCount++;
                _manifestSemanticSha256.Add(document.DocumentSemanticSha256);
                var atom = document.Atom!;
                _atomHashes.Add(new AtomHash(
                    atom.IdentitySha256,
                    atom.AtomKind,
                    atom.ValueIdentitySha256,
                    atom.AtomSemanticSha256));
                var bucketKey = new BusinessBucketKey(
                    atom.Family,
                    atom.Kind,
                    atom.AtomKind,
                    atom.ValueType,
                    atom.ValueState);
                _businessBuckets[bucketKey] = checked(
                    _businessBuckets.GetValueOrDefault(bucketKey) + 1L);
                break;
            case StatisticReconciliationObservationRecordKinds.GenerationCommit:
                if (_commit is not null)
                    throw Invalid("EXACT_COMMIT_REQUIRED");
                _commit = document;
                break;
            default:
                throw Invalid("RECORD_KIND_INVALID");
        }
    }

    internal StatisticReconciliationObservation Complete()
    {
        if (_completed)
            throw Invalid("GENERATION_VALIDATION_ALREADY_COMPLETED");
        _completed = true;
        var commitDocument = _commit ?? throw Invalid("EXACT_COMMIT_REQUIRED");
        var commit = commitDocument.Commit ?? throw Invalid("COMMIT_PAYLOAD_REQUIRED");
        var expectedDocumentCount =
            StatisticReconciliationExpectedObservationIntegrity
                .RequireGenerationDocumentCount(
                    _sourceDecisionCount,
                    _configurationPinCount,
                    _lineagePinCount,
                    _atomCount);
        if (_documentCount != expectedDocumentCount ||
            commit.SourceDecisionCount != _sourceDecisionCount ||
            commit.ConfigurationPinCount != _configurationPinCount ||
            commit.LineagePinCount != _lineagePinCount ||
            commit.AtomCount != _atomCount ||
            commit.DocumentCount != expectedDocumentCount)
            throw Invalid("COMMIT_COUNT_MISMATCH");

        var manifestSha256 =
            StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
                "P10_EXPECTED_OBSERVATION_MANIFEST_V1",
                _manifestSemanticSha256);
        if (!string.Equals(
                commit.ManifestSha256,
                manifestSha256,
                StringComparison.Ordinal))
            throw Invalid("MANIFEST_SHA256_MISMATCH");

        var typedSemanticSha256 =
            StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
                "P10_EXPECTED_TYPED_LEDGER_V1",
                _atomHashes
                    .OrderBy(item => item.IdentitySha256, StringComparer.Ordinal)
                    .ThenBy(item => item.AtomKind, StringComparer.Ordinal)
                    .ThenBy(item => item.ValueIdentitySha256, StringComparer.Ordinal)
                    .Select(item => item.AtomSemanticSha256));
        if (!string.Equals(
                commitDocument.TypedSemanticSha256,
                typedSemanticSha256,
                StringComparison.Ordinal))
            throw Invalid("TYPED_SEMANTIC_SHA256_MISMATCH");

        var generationSemanticSha256 =
            StatisticReconciliationExpectedObservationIntegrity
                .BuildGenerationSemanticSha256(commitDocument);
        if (!string.Equals(
                commitDocument.GenerationSemanticSha256,
                generationSemanticSha256,
                StringComparison.Ordinal))
            throw Invalid("GENERATION_SEMANTIC_SHA256_MISMATCH");
        var generationId =
            StatisticReconciliationExpectedObservationIntegrity.BuildGenerationId(
                commitDocument.ReconciliationId,
                generationSemanticSha256);
        if (!string.Equals(
                commitDocument.GenerationId,
                generationId,
                StringComparison.Ordinal))
            throw Invalid("GENERATION_ID_MISMATCH");
        BusinessBuckets = _businessBuckets
            .OrderBy(item => item.Key.Family, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Key.AtomKind, StringComparer.Ordinal)
            .ThenBy(item => item.Key.ValueType, StringComparer.Ordinal)
            .ThenBy(item => item.Key.ValueState, StringComparer.Ordinal)
            .Select(item => new StatisticReconciliationExpectedBusinessBucket(
                item.Key.Family,
                item.Key.Kind,
                item.Key.AtomKind,
                item.Key.ValueType,
                item.Key.ValueState,
                item.Value))
            .ToArray();
        return commitDocument;
    }

    private static StatisticReconciliationExpectedObservationIntegrityException Invalid(
        string reason)
        => new(reason);

    private sealed record BusinessBucketKey(
        string Family,
        string Kind,
        string AtomKind,
        string ValueType,
        string ValueState);

    private sealed record AtomHash(
        string IdentitySha256,
        string AtomKind,
        string ValueIdentitySha256,
        string AtomSemanticSha256);
}
internal sealed record StatisticReconciliationExpectedBusinessBucket(
    string Family,
    string Kind,
    string AtomKind,
    string ValueType,
    string ValueState,
    long AtomCount);