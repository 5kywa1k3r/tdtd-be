using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualExportFormats
{
    internal const string Csv = "CSV";
    internal const string Xlsx = "XLSX";
}

internal static class StatisticReconciliationActualExportValueTypes
{
    internal const string Integer = "INTEGER";
    internal const string Decimal = "DECIMAL";
    internal const string Boolean = "BOOLEAN";
    internal const string UtcInstant = "UTC_INSTANT";
    internal const string Text = "TEXT";
    internal const string Json = "JSON";

    internal static bool IsSupported(string value)
        => value is Integer or Decimal or Boolean or UtcInstant or Text or Json;
}

internal static class StatisticReconciliationActualExportBlankPolicies
{
    internal const string Forbidden = "FORBIDDEN";
    internal const string Null = "NULL";
    internal const string Empty = "EMPTY";

    internal static bool IsSupported(string value)
        => value is Forbidden or Null or Empty;
}

internal static class StatisticReconciliationActualExportValueStates
{
    internal const string Value = "VALUE";
    internal const string Null = "NULL";
    internal const string Empty = "EMPTY";
}

internal sealed record StatisticReconciliationActualExportColumnContract(
    int Ordinal,
    string Name,
    string ValueType,
    string BlankPolicy,
    bool IsFullFilterTotal = false);

/// <summary>
/// Immutable metadata read from the authoritative P9 export artifact owner.
/// It is deliberately separate from the exported bytes so the parser can
/// validate both the manifest digest and the content digest independently.
/// </summary>
internal sealed record StatisticReconciliationActualExportManifest(
    string SchemaVersion,
    string ExportId,
    string RequestSha256,
    string AuthorizationSnapshotSha256,
    string Format,
    string ResultKind,
    string ContentType,
    string FileName,
    string ContentSha256,
    long ByteCount,
    int RowCount,
    int ColumnCount,
    string WorkId,
    string ScopeType,
    string ScopeId,
    string ResultId,
    string ResultSha256,
    string ConfigSha256,
    string SourceSha256,
    string FilterSha256,
    int LifecycleRevision,
    string CatalogVersion,
    string CatalogRawSha256,
    string CatalogSemanticSha256,
    string StageLockSha256,
    string CandidateChainId,
    string CandidatePromptId,
    int CandidateStage,
    string OwnerSemanticSha256,
    DateTime CompletedAtUtc,
    ImmutableArray<StatisticReconciliationActualExportColumnContract> Columns,
    string? PeriodInstanceKey = null,
    string? CanonicalFilterJson = null);

internal sealed record StatisticReconciliationActualExportArtifact(
    StatisticReconciliationActualExportManifest Manifest,
    string ManifestSha256,
    ReadOnlyMemory<byte> Content);

internal sealed record StatisticReconciliationActualExportCellObservation(
    int ColumnOrdinal,
    string ColumnName,
    string ValueType,
    string ValueState,
    string CanonicalValue,
    int DecimalScale,
    bool FormulaNeutralized,
    string CellSemanticSha256);

internal sealed record StatisticReconciliationActualExportRowObservation(
    int Ordinal,
    ImmutableArray<StatisticReconciliationActualExportCellObservation> Cells,
    string RowSemanticSha256);

internal sealed record StatisticReconciliationActualExportTotalObservation(
    string Name,
    string ValueType,
    string ValueState,
    string CanonicalValue,
    int DecimalScale,
    string TotalSemanticSha256);

internal sealed record StatisticReconciliationActualExportCapture(
    string ExportId,
    string Format,
    string ResultKind,
    string ResultId,
    string ResultSha256,
    string ConfigSha256,
    string SourceSha256,
    string FilterSha256,
    int LifecycleRevision,
    string ContentSha256,
    string ManifestSha256,
    string OwnerSemanticSha256,
    ImmutableArray<string> Headers,
    ImmutableArray<StatisticReconciliationActualExportRowObservation> Rows,
    ImmutableArray<StatisticReconciliationActualExportTotalObservation> FullFilterTotals,
    string RowsSemanticSha256,
    string TotalsSemanticSha256,
    string CaptureSemanticSha256,
    string? PeriodInstanceKey = null,
    string? CanonicalFilterJson = null);
