using System.Text.Json.Serialization;
using tdtd_be.DTOs.DynamicForms;

namespace tdtd_be.DTOs.Statistics;

public sealed record NativeStatisticResultRequest(string WorkId, string PeriodInstanceKey,
    string DynamicFormTemplateId, string? RunId = null, string? GenerationId = null, bool Historical = false);

// Complete native result document. No paged CONCAT fragments presented as totals.
// Historical=true requires an exact run + generation and is labelled HISTORICAL.
public sealed record NativeStatisticResultResponse(P9DirectResultMetadata Metadata,
    NativeStatisticResultDocument? Result, string? ArtifactHash)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NativeStatisticResultMetadata? NativeMetadata { get; init; }
}

// Derived only from the same verified snapshot as the result. ContentHash covers
// version/schemaHash/planContentDigest/targets, not itself or wall-clock fields.
public sealed record NativeStatisticResultMetadata(int Version, string SchemaHash, string PlanContentDigest,
    string ContentHash, IReadOnlyList<NativeStatisticTargetMetadata> Targets);
public sealed record NativeStatisticTargetMetadata(DynamicFormNativeStatisticPlanTargetDto Configuration,
    string StructureHash, IReadOnlyList<DynamicFormStatisticLabelSnapshotDto> LabelSnapshots,
    NativeStatisticSectionMetadata Section, NativeStatisticTableMetadata Table, IReadOnlyList<NativeStatisticCellMetadata> Cells);
public sealed record NativeStatisticSectionMetadata(string Id, string? Title);
public sealed record NativeStatisticTableMetadata(string Id, string Name, int Order, string Layout,
    IReadOnlyList<DynamicFormNativeColumnDto> Fields, IReadOnlyList<DynamicFormNativeRowDto> Rows);
public sealed record NativeStatisticCellMetadata(string FieldId, string? RowId, DynamicFormNativeCellSpecDto Spec);
public sealed record NativeStatisticResultDocument(int Version, string SchemaHash, string PlanContentDigest,
    string SourceOrderDigest, IReadOnlyList<NativeStatisticSourceDto> Sources, IReadOnlyList<NativeStatisticGroupDto> Groups);
public sealed record NativeStatisticSourceDto(string ReportId, int PayloadRevision, string PayloadHash, DateTime? PayloadUpdatedAtUtc);
public sealed record NativeStatisticGroupDto(NativeStatisticAddressDto Address, IReadOnlyList<NativeStatisticOperationDto> Operations);
public sealed record NativeStatisticAddressDto(string TableId, string TargetId, string? RowId, string? FieldId, string? ReportId, string? RecordId);
public sealed record NativeStatisticCountsDto(long SourceReports, long PresentReports, long CellCount, long PresentCount,
    long MissingCount, long NullCount, long ExcludedCellCount);
public sealed record NativeStatisticValueDto(string Type, string State, string? Text, bool? Boolean, IReadOnlyList<string>? Items);
public sealed record NativeStatisticNumericDto(string Sum, long Count);
public sealed record NativeStatisticCellDto(string ReportId, string TableId, string? RowId, string? RecordId, string FieldId);
public sealed record NativeStatisticBucketDto(string Type, string Code, string Label, long Count);
public sealed record NativeStatisticTextChunkDto(int Index, string Text);
public sealed record NativeStatisticTextSegmentDto(long Index, long StartUtf16, int LengthUtf16, NativeStatisticCellDto Source);
public sealed record NativeStatisticStackCellDto(long Row, long Column, NativeStatisticCellDto Source, NativeStatisticValueDto Value);
public sealed record NativeStatisticStackChunkDto(int Index, IReadOnlyList<NativeStatisticStackCellDto> Cells);
public sealed record NativeStatisticStackBlockDto(string ReportId, long RowOffset, long ColumnOffset, int Rows, int Columns);
public sealed record NativeStatisticStackDto(string Orientation, long Rows, long Columns, IReadOnlyList<string> FieldIds,
    IReadOnlyList<string> MatrixRowIds, IReadOnlyList<NativeStatisticStackBlockDto> Blocks, IReadOnlyList<NativeStatisticStackChunkDto> Chunks);
public sealed record NativeStatisticOperationDto(string OperationId, string Method, string Kind, NativeStatisticCountsDto Counts,
    NativeStatisticValueDto? Value, NativeStatisticNumericDto? Numeric, NativeStatisticCellDto? SelectedSource,
    IReadOnlyList<NativeStatisticBucketDto>? Buckets, IReadOnlyList<NativeStatisticTextChunkDto>? TextChunks,
    long? SegmentCount, IReadOnlyList<NativeStatisticTextSegmentDto>? TextSegments, NativeStatisticStackDto? Stack);
