using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

// Internal calculation contract, not a published result/API or a generation receipt.
// L5c supplies authorized INCLUDED sources, persists/revalidates pins and maps results.
internal sealed record NativeStatisticCalculationLimits(
    int MaxReports, int MaxGroups, int MaxCellVisits,
    int MaxRetainedBytes, int MaxOutputBytes, int ChunkBytes);

internal sealed class NativeStatisticCalculationSource
{
    internal WorkReportNativeSourcePin Pin { get; }
    internal JsonElement Values { get; }
    private NativeStatisticCalculationSource(WorkReportNativeSourcePin pin, JsonElement values)
        => (Pin, Values) = (pin, values);

    internal static NativeStatisticCalculationSource Capture(WorkAssignmentReport report, WorkReportPayloadSnapshot payload)
    {
        var pin = WorkReportNativeSourcePin.Capture(report, payload);
        if (payload.TableValuesJson is null
            || NativeStatisticText.Utf8Bytes(payload.TableValuesJson) > DynamicFormNativeTableValues.MaximumBytes)
            throw NativeStatisticCalculationError.Invalid("SOURCE_VALUES_REQUIRED_OR_TOO_LARGE");
        using var document = JsonDocument.Parse(payload.TableValuesJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count(p => p.Name == "nativeTables") != 1)
            throw NativeStatisticCalculationError.Invalid("SOURCE_NATIVE_ENVELOPE_REQUIRED");
        return new(pin, root.GetProperty("nativeTables").Clone());
    }
}

internal sealed record NativeStatisticCellReference(
    string ReportId, string TableId, string? RowId, string? RecordId, string FieldId);

// Numbers are invariant exact decimal strings; dates retain original precision/literal.
// Missing, explicit null, false, zero, empty text and empty lists remain distinct.
internal sealed record NativeStatisticValue(string Type, string State,
    string? Text = null, bool? Boolean = null, IReadOnlyList<string>? Items = null);

internal sealed record NativeStatisticGroupAddress(string TableId, string TargetId,
    string? RowId, string? FieldId, string? ReportId, string? RecordId);

internal sealed record NativeStatisticCounts(long SourceReports, long PresentReports,
    long CellCount, long PresentCount, long MissingCount, long NullCount, long ExcludedCellCount);

// AVG remains the exact fraction Sum / Count. L5c must choose/display rounding explicitly.
// Counts are counts of numeric cells, never counts of pre-averaged child summaries.
internal sealed record NativeStatisticNumericSummary(string Sum, long Count);
internal sealed record NativeStatisticBucket(string Type, string Code, string Label, long Count);
internal sealed record NativeStatisticTextChunk(int Index, string Text);
// Offsets address the exact concatenation of chunk.Text in UTF-16 code units.
// Separators belong to operation options; zero-length kept segments still have provenance.
internal sealed record NativeStatisticTextSegment(long Index, long StartUtf16, int LengthUtf16, NativeStatisticCellReference Source);
internal sealed record NativeStatisticStackCell(long Row, long Column,
    NativeStatisticCellReference Source, NativeStatisticValue Value);
internal sealed record NativeStatisticStackChunk(int Index, IReadOnlyList<NativeStatisticStackCell> Cells);
internal sealed record NativeStatisticStackBlock(string ReportId, long RowOffset, long ColumnOffset, int Rows, int Columns);
internal sealed record NativeStatisticStack(string Orientation, long Rows, long Columns,
    IReadOnlyList<string> FieldIds, IReadOnlyList<string> MatrixRowIds,
    IReadOnlyList<NativeStatisticStackBlock> Blocks, IReadOnlyList<NativeStatisticStackChunk> Chunks);

internal sealed record NativeStatisticOperationResult(string OperationId, string Method, string Kind,
    NativeStatisticCounts Counts, NativeStatisticValue? Value = null,
    NativeStatisticNumericSummary? Numeric = null, NativeStatisticCellReference? SelectedSource = null,
    IReadOnlyList<NativeStatisticBucket>? Buckets = null,
    IReadOnlyList<NativeStatisticTextChunk>? TextChunks = null, long? SegmentCount = null,
    IReadOnlyList<NativeStatisticTextSegment>? TextSegments = null,
    NativeStatisticStack? Stack = null);

internal sealed record NativeStatisticGroupResult(NativeStatisticGroupAddress Address,
    IReadOnlyList<NativeStatisticOperationResult> Operations);

internal sealed record NativeStatisticCalculationResult(int Version, string SchemaHash,
    string PlanContentDigest, string SourceOrderDigest, IReadOnlyList<WorkReportNativeSourcePin> Sources,
    IReadOnlyList<NativeStatisticGroupResult> Groups);

internal static class NativeStatisticCalculationError
{
    internal static Exception Invalid(string reason)
        => AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED,
            new { scope = "nativeStatisticCalculation", reason });
}

internal sealed class NativeStatisticCalculationBudget
{
    internal NativeStatisticCalculationLimits Limits { get; }
    private long _retained;
    private long _visits;
    private int _groups;
    private readonly CancellationToken _cancellationToken;
    internal NativeStatisticCalculationBudget(NativeStatisticCalculationLimits limits, CancellationToken cancellationToken)
    {
        if (limits.MaxReports < 1 || limits.MaxGroups < 1 || limits.MaxCellVisits < 1
            || limits.MaxRetainedBytes < 1 || limits.MaxOutputBytes < 1
            || limits.ChunkBytes < 4 || limits.ChunkBytes > limits.MaxOutputBytes)
            throw NativeStatisticCalculationError.Invalid("EXPLICIT_CALCULATION_LIMITS_REQUIRED");
        Limits = limits;
        _cancellationToken = cancellationToken;
    }
    internal void CheckCancellation() => _cancellationToken.ThrowIfCancellationRequested();
    internal void Retain(long bytes)
    {
        CheckCancellation();
        if (bytes < 0 || bytes > Limits.MaxRetainedBytes - _retained)
            throw NativeStatisticCalculationError.Invalid("CALCULATION_RETAINED_BYTES_LIMIT");
        _retained += bytes;
    }
    internal void Visit()
    {
        if (++_visits > Limits.MaxCellVisits) throw NativeStatisticCalculationError.Invalid("CALCULATION_CELL_VISIT_LIMIT");
    }
    internal void Group()
    {
        if (++_groups > Limits.MaxGroups) throw NativeStatisticCalculationError.Invalid("CALCULATION_GROUP_LIMIT");
        Retain(256);
    }
}
