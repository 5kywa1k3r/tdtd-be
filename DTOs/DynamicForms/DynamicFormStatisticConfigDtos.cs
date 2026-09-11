using System.Text.Json;
using System.Text.Json.Serialization;
using tdtd_be.DTOs.StatisticsConfiguration;

namespace tdtd_be.DTOs.DynamicForms;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormStatisticConfigPayload(
    IReadOnlyList<DynamicFormStatisticFieldPayload>? Fields,
    IReadOnlyList<DynamicFormStatisticTablePayload>? Tables);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormStatisticTablePayload(
    string? BlockId,
    string? TableMode,
    bool? StatisticsDisabled,
    IReadOnlyList<DynamicFormStatisticTableMetricPayload>? Metrics,
    IReadOnlyList<DynamicFormStatisticMetricLabelTargetPayload>?
        MetricLabelTargets,
    IReadOnlyList<string>? AllowedRowLabelCodes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormStatisticTableMetricPayload(
    string? MetricKey,
    string? DataType,
    IReadOnlyList<string>? AggregateOps);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormStatisticMetricLabelTargetPayload(
    string? MetricKey,
    string? StatisticLabelCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormStatisticFieldPayload(
    string? FieldId,
    bool? IsStatistic,
    DynamicFormStatisticSettingsPayload? Statistic,
    IReadOnlyList<string>? StatisticLabelCodes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormStatisticSettingsPayload(
    IReadOnlyList<string>? AggregateOps,
    string? BucketMode,
    bool? ShowInDetail,
    bool? ShowInTree);

public sealed record DynamicFormStatisticConfigResult(
    string OwnerKind,
    string OwnerId,
    string ConfigId,
    string VersionId,
    int VersionNo,
    long Revision,
    string Status,
    string ConfigHash,
    IReadOnlyList<string> DependencyPins,
    StatConfigPermissionSet Permissions,
    IReadOnlyList<DynamicFormStatisticFieldConfigDto> Fields,
    IReadOnlyList<DynamicFormStatisticTableConfigDto> TableConfig,
    string FieldSectionHash,
    string TableSectionHash,
    IReadOnlyList<DynamicFormStatisticConfigVersionSnapshotDto> Versions,
    string? ReceiptId);

public sealed record DynamicFormStatisticFieldConfigDto(
    string FieldId,
    string FieldType,
    bool IsStatistic,
    DynamicFormStatisticSettingsPayload? Statistic,
    IReadOnlyList<string> StatisticLabelCodes,
    IReadOnlyList<DynamicFormStatisticLabelSnapshotDto> LabelSnapshots,
    string StructureHash);

public sealed record DynamicFormStatisticLabelSnapshotDto(
    string LabelId,
    string Code,
    string DataType,
    string Usage,
    string ScopeType,
    string? ScopeId,
    bool IsActive,
    int VersionNo,
    string VersionId,
    string ConfigHash);

public sealed record DynamicFormStatisticTableConfigDto(
    string BlockId,
    string TableMode,
    bool StatisticsDisabled,
    int? StatisticsInputCellCount,
    int? StatisticsInputCellLimit,
    string? StatisticsDisabledReason,
    string RowLabelDataType,
    IReadOnlyList<DynamicFormStatisticTableMetricConfigDto> Metrics,
    IReadOnlyList<DynamicFormStatisticMetricLabelTargetConfigDto>
        MetricLabelTargets,
    IReadOnlyList<string> AllowedRowLabelCodes,
    IReadOnlyList<DynamicFormStatisticLabelSnapshotDto>
        RowLabelSnapshots,
    string StructureHash,
    string SchemaSource);

public sealed record DynamicFormStatisticTableMetricConfigDto(
    string MetricKey,
    string DataType,
    IReadOnlyList<string> AggregateOps);

public sealed record DynamicFormStatisticMetricLabelTargetConfigDto(
    string MetricKey,
    string StatisticLabelCode,
    string DataType,
    DynamicFormStatisticLabelSnapshotDto LabelSnapshot);

public sealed record DynamicFormStatisticConfigVersionSnapshotDto(
    string VersionId,
    string? PreviousVersionId,
    int VersionNo,
    long Revision,
    string Status,
    string ConfigHash,
    IReadOnlyList<string> DependencyPins,
    IReadOnlyList<DynamicFormStatisticFieldConfigDto> Fields,
    IReadOnlyList<DynamicFormStatisticTableConfigDto> TableConfig,
    string FieldSectionHash,
    string TableSectionHash,
    DateTime CreatedAtUtc);

public static class DynamicFormStatisticFieldTypes
{
    public const string Number = "NUMBER";
    public const string Date = "DATE";
    public const string FullDate = "FULL_DATE";
    public const string Boolean = "BOOLEAN";
    public const string SingleSelect = "SINGLE_SELECT";
    public const string MultiSelect = "MULTI_SELECT";
    public const string ShortText = "SHORT_TEXT";
    public const string LongText = "LONG_TEXT";
    public const string StringList = "STRING_LIST";
    public const string RichText = "RICH_TEXT";
}

public static class DynamicFormStatisticAggregateOperations
{
    public const string Count = "COUNT";
    public const string Sum = "SUM";
    public const string Average = "AVG";
    public const string Minimum = "MIN";
    public const string Maximum = "MAX";
    public const string Latest = "LATEST";
    public const string TrueCount = "TRUE_COUNT";
    public const string FalseCount = "FALSE_COUNT";
    public const string BucketCount = "BUCKET_COUNT";
    public const string Concat = "CONCAT";
}

public static class DynamicFormStatisticBucketModes
{
    public const string None = "NONE";
    public const string Option = "OPTION";
    public const string Date = "DATE";
}

public static class DynamicFormTableStatisticOperationContract
{
    public const int MaximumBlocks = 30;
    public const int MaximumTargets = 30;

    public const string FixedGrid = "FIXED_GRID";
    public const string AppendRows = "APPEND_ROWS";
    public const string AppendColumns = "APPEND_COLUMNS";
    public const string Matrix = "MATRIX";
    public const string SummaryTemplate = "SUMMARY_TEMPLATE";

    public const string Number = "NUMBER";
    public const string ShortText = "SHORT_TEXT";
    public const string MultiSelect = "MULTI_SELECT";
    public const string Boolean = "BOOLEAN";
    public const string Date = "DATE";
    public const string FullDate = "FULL_DATE";
    public const string Ignore = "IGNORE";

    public const string Count = "COUNT";
    public const string Sum = "SUM";
    public const string Minimum = "MIN";
    public const string Maximum = "MAX";
    public const string Average = "AVERAGE";
    public const string BucketCount = "BUCKET_COUNT";
    public const string TrueCount = "TRUE_COUNT";
    public const string FalseCount = "FALSE_COUNT";
    public const string Earliest = "EARLIEST";
    public const string Latest = "LATEST";

    private static readonly IReadOnlySet<string> Modes =
        Set(FixedGrid, AppendRows, AppendColumns, Matrix);

    private static readonly IReadOnlyDictionary<
        string,
        IReadOnlySet<string>> Operations =
        new Dictionary<string, IReadOnlySet<string>>(
            StringComparer.Ordinal)
        {
            [Number] = Set(
                Count,
                Sum,
                Minimum,
                Maximum,
                Average),
            [ShortText] = Set(Count, BucketCount),
            [MultiSelect] = Set(Count, BucketCount),
            [Boolean] = Set(Count, TrueCount, FalseCount),
            [Date] = Set(Count, Earliest, Latest),
            [FullDate] = Set(Count, Earliest, Latest)
        };

    public static IReadOnlySet<string> AllowedInputModes => Modes;

    public static IReadOnlyDictionary<string, IReadOnlySet<string>>
        AllowedOperationsByDataType => Operations;

    public static bool IsInputMode(string mode)
        => Modes.Contains(mode);

    public static bool IsSupportedDataType(string dataType)
        => Operations.ContainsKey(dataType);

    public static bool IsOperationAllowed(
        string dataType,
        string operation)
        => Operations.TryGetValue(dataType, out var allowed) &&
           allowed.Contains(operation);

    private static IReadOnlySet<string> Set(params string[] values)
        => new HashSet<string>(values, StringComparer.Ordinal);
}

/// <summary>
/// Frozen FEATURE_06/P8-02 compatibility contract. Callers must reject an
/// explicit operation or bucket mode that is absent; no fallback is allowed.
/// </summary>
public static class DynamicFormStatisticOperationContract
{
    public const int MaximumTargets = 30;

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Operations =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [DynamicFormStatisticFieldTypes.Number] = Set(
                DynamicFormStatisticAggregateOperations.Count,
                DynamicFormStatisticAggregateOperations.Sum,
                DynamicFormStatisticAggregateOperations.Average,
                DynamicFormStatisticAggregateOperations.Minimum,
                DynamicFormStatisticAggregateOperations.Maximum,
                DynamicFormStatisticAggregateOperations.Latest),
            [DynamicFormStatisticFieldTypes.Date] = Set(
                DynamicFormStatisticAggregateOperations.Count,
                DynamicFormStatisticAggregateOperations.Minimum,
                DynamicFormStatisticAggregateOperations.Maximum,
                DynamicFormStatisticAggregateOperations.Latest),
            [DynamicFormStatisticFieldTypes.FullDate] = Set(
                DynamicFormStatisticAggregateOperations.Count,
                DynamicFormStatisticAggregateOperations.Minimum,
                DynamicFormStatisticAggregateOperations.Maximum,
                DynamicFormStatisticAggregateOperations.Latest),
            [DynamicFormStatisticFieldTypes.Boolean] = Set(
                DynamicFormStatisticAggregateOperations.Count,
                DynamicFormStatisticAggregateOperations.TrueCount,
                DynamicFormStatisticAggregateOperations.FalseCount),
            [DynamicFormStatisticFieldTypes.SingleSelect] = Set(
                DynamicFormStatisticAggregateOperations.Count,
                DynamicFormStatisticAggregateOperations.BucketCount,
                DynamicFormStatisticAggregateOperations.Latest),
            [DynamicFormStatisticFieldTypes.MultiSelect] = Set(
                DynamicFormStatisticAggregateOperations.Count,
                DynamicFormStatisticAggregateOperations.BucketCount),
            [DynamicFormStatisticFieldTypes.ShortText] = Set(
                DynamicFormStatisticAggregateOperations.Count,
                DynamicFormStatisticAggregateOperations.Latest,
                DynamicFormStatisticAggregateOperations.Concat),
            [DynamicFormStatisticFieldTypes.LongText] = Set(
                DynamicFormStatisticAggregateOperations.Count,
                DynamicFormStatisticAggregateOperations.Latest,
                DynamicFormStatisticAggregateOperations.Concat),
            [DynamicFormStatisticFieldTypes.StringList] = Set(
                DynamicFormStatisticAggregateOperations.Count)
        };

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedOperationsByFieldType
        => Operations;

    public static bool IsSupportedFieldType(string fieldType)
        => Operations.ContainsKey(fieldType);

    public static bool IsOperationAllowed(string fieldType, string operation)
        => Operations.TryGetValue(fieldType, out var allowed) && allowed.Contains(operation);

    public static bool IsBucketModeAllowed(string fieldType, string bucketMode)
    {
        if (!Operations.ContainsKey(fieldType))
        {
            return false;
        }

        return bucketMode switch
        {
            DynamicFormStatisticBucketModes.None => true,
            DynamicFormStatisticBucketModes.Option => fieldType is
                DynamicFormStatisticFieldTypes.SingleSelect or
                DynamicFormStatisticFieldTypes.MultiSelect,
            DynamicFormStatisticBucketModes.Date => fieldType is
                DynamicFormStatisticFieldTypes.Date or
                DynamicFormStatisticFieldTypes.FullDate,
            _ => false
        };
    }

    private static IReadOnlySet<string> Set(params string[] values)
        => new HashSet<string>(values, StringComparer.Ordinal);
}
