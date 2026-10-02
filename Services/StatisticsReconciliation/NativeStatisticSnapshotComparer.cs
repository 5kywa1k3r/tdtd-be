using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.StatisticsReconciliation;

internal sealed record NativeComparisonIdentity(string ScopeAssignmentId, string DynamicFormTemplateId,
    string SectionId, string Grain, string GrainKey, string TimeAxis, DateTime StartUtc, DateTime EndExclusiveUtc,
    StatConfigIdentity Configuration, string SourceSetHash, string MetadataHash);
internal sealed record NativeComparisonInput(NativeComparisonIdentity Identity, NativeStatisticResultDocument Result);
internal sealed record NativeComparisonDifference(string Reason, NativeStatisticAddressDto? Address = null, string? OperationId = null);
internal sealed record NativeComparisonResult(bool Comparable, IReadOnlyList<NativeComparisonDifference> Differences)
{
    internal bool Equivalent => Comparable && Differences.Count == 0;
}

// Pure comparison of independently verified captures. NOT an API trust boundary,
// source calculator, hash authenticator, freshness check or production verdict.
// Callers must validate complete artifacts/metadata and ACL before constructing inputs.
internal static class NativeStatisticSnapshotComparer
{
    private const int MaximumOperations = 50000;
    private const int MaximumDifferences = 256;
    private static string Canonical(object? value) => StatConfigCanonicalJson.Canonicalize(value);

    internal static NativeComparisonResult Compare(NativeComparisonInput expected, NativeComparisonInput actual)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(actual);
        var differences = new List<NativeComparisonDifference>();
        void Add(string reason, NativeStatisticAddressDto? address = null, string? operationId = null)
        {
            if (differences.Count == MaximumDifferences) throw new InvalidOperationException("NATIVE_COMPARISON_DIFFERENCE_LIMIT");
            differences.Add(new(reason, address, operationId));
        }
        var e = expected.Result; var a = actual.Result;
        if (Canonical(expected.Identity) != Canonical(actual.Identity)) Add("CAPTURE_IDENTITY_CHANGED");
        if (e.Version != a.Version || e.SchemaHash != a.SchemaHash) Add("SCHEMA_CHANGED");
        if (e.PlanContentDigest != a.PlanContentDigest) Add("PLAN_CHANGED");
        if (e.SourceOrderDigest != a.SourceOrderDigest || Canonical(e.Sources) != Canonical(a.Sources)) Add("SOURCES_CHANGED");
        if (differences.Count > 0) return new(false, differences);

        var left = Index(e); var right = Index(a);
        foreach (var key in left.Keys.Union(right.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!left.TryGetValue(key, out var l)) { var extra = right[key]; Add("UNEXPECTED_OPERATION", extra.Address, extra.Operation.OperationId); continue; }
            if (!right.TryGetValue(key, out var r)) { Add("MISSING_OPERATION", l.Address, l.Operation.OperationId); continue; }
            if (Canonical(Content(l.Operation)) != Canonical(Content(r.Operation))) Add("OPERATION_CHANGED", l.Address, l.Operation.OperationId);
        }
        return new(true, differences);
    }

    private static Dictionary<string, (NativeStatisticAddressDto Address, NativeStatisticOperationDto Operation)> Index(NativeStatisticResultDocument document)
    {
        var result = new Dictionary<string, (NativeStatisticAddressDto, NativeStatisticOperationDto)>(StringComparer.Ordinal);
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in document.Groups)
        {
            if (!groups.Add(Canonical(group.Address)) || group.Operations.Count == 0) throw new InvalidOperationException("NATIVE_COMPARISON_GROUP_INVALID");
            foreach (var op in group.Operations)
            {
                if (string.IsNullOrWhiteSpace(op.OperationId) || !result.TryAdd(Canonical(new { group.Address, op.OperationId }), (group.Address, op)))
                    throw new InvalidOperationException("NATIVE_COMPARISON_OPERATION_INVALID");
                if (result.Count > MaximumOperations) throw new InvalidOperationException("NATIVE_COMPARISON_OPERATION_LIMIT");
                _ = Content(op); // Validate chunk sequencing even for missing/extra operations.
            }
        }
        return result;
    }

    private static object Content(NativeStatisticOperationDto op)
    {
        // Transport chunk boundaries are irrelevant; logical text, segment offsets,
        // cell order/coordinates, blocks and source references remain exact.
        if (op.TextChunks is not null && op.TextChunks.Where((c, i) => c.Index != i).Any()
            || op.Stack is not null && op.Stack.Chunks.Where((c, i) => c.Index != i).Any())
            throw new InvalidOperationException("NATIVE_COMPARISON_CHUNKS_INVALID");
        return new {
            op.Method, op.Kind, op.Counts, op.Value, op.Numeric, op.SelectedSource, op.Buckets,
            text = op.TextChunks is null ? null : string.Concat(op.TextChunks.Select(c => c.Text)),
            op.SegmentCount, op.TextSegments,
            stack = op.Stack is null ? null : new {
                op.Stack.Orientation, op.Stack.Rows, op.Stack.Columns, op.Stack.FieldIds, op.Stack.MatrixRowIds,
                op.Stack.Blocks, cells = op.Stack.Chunks.SelectMany(c => c.Cells).ToArray()
            }
        };
    }
}
