using System.Text.RegularExpressions;
using tdtd_be.Common.Errors;
using tdtd_be.Models;

namespace tdtd_be.Services.StatisticsConfiguration;

public sealed record LockedLabelSnapshot(
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

public static class LabelLayerKinds
{
    public const string ClassificationTag = "CLASSIFICATION_TAG";
    public const string FieldStatistic = "FIELD_STATISTIC";
    public const string TableMetric = "TABLE_METRIC";
    public const string RuntimeRow = "RUNTIME_ROW";
}

/// <summary>
/// Strict P8 taxonomy entrypoint. Legacy adapters may remain readable, but all
/// new config commands call only these exact normalizers.
/// </summary>
public static class LabelTaxonomyContract
{
    private static readonly Regex CodeRegex = new(
        "^[a-z0-9][a-z0-9_.-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string NormalizeCode(string? value, string path)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) ||
            !CodeRegex.IsMatch(normalized))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.LABEL_CODE_INVALID,
                new { path, value });
        }
        return normalized;
    }

    public static string NormalizeScopeType(string? value, string path)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return normalized switch
        {
            LabelScopeTypes.Global => LabelScopeTypes.Global,
            LabelScopeTypes.Level => LabelScopeTypes.Level,
            LabelScopeTypes.Unit => LabelScopeTypes.Unit,
            _ => throw AppExceptionFactory.BadRequest(
                AppErrorCode.LABEL_SCOPE_TYPE_INVALID,
                new { path, value })
        };
    }

    public static string NormalizeUsage(string? value, string path)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return normalized switch
        {
            LabelUsages.Classification => LabelUsages.Classification,
            LabelUsages.Statistic => LabelUsages.Statistic,
            LabelUsages.TableTarget => LabelUsages.TableTarget,
            _ => throw AppExceptionFactory.BadRequest(
                AppErrorCode.LABEL_USAGE_INVALID,
                new { path, value })
        };
    }

    public static string NormalizeDataType(string? value, string path)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return normalized switch
        {
            LabelDataTypes.Number => LabelDataTypes.Number,
            LabelDataTypes.ShortText => LabelDataTypes.ShortText,
            LabelDataTypes.StringList => LabelDataTypes.StringList,
            LabelDataTypes.LongText => LabelDataTypes.LongText,
            LabelDataTypes.Date => LabelDataTypes.Date,
            LabelDataTypes.Boolean => LabelDataTypes.Boolean,
            _ => throw AppExceptionFactory.BadRequest(
                AppErrorCode.LABEL_DATA_TYPE_INVALID,
                new { path, value })
        };
    }

    public static string NormalizeValueSourceType(
        string? value,
        string path)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return normalized switch
        {
            LabelValueSourceTypes.None => LabelValueSourceTypes.None,
            LabelValueSourceTypes.FixedEnum =>
                LabelValueSourceTypes.FixedEnum,
            LabelValueSourceTypes.EnumCatalog =>
                LabelValueSourceTypes.EnumCatalog,
            LabelValueSourceTypes.SystemUnit =>
                LabelValueSourceTypes.SystemUnit,
            LabelValueSourceTypes.SystemUser =>
                LabelValueSourceTypes.SystemUser,
            LabelValueSourceTypes.SystemPosition =>
                LabelValueSourceTypes.SystemPosition,
            LabelValueSourceTypes.SystemUnitType =>
                LabelValueSourceTypes.SystemUnitType,
            _ => throw AppExceptionFactory.BadRequest(
                AppErrorCode.LABEL_VALUE_SOURCE_TYPE_INVALID,
                new { path, value })
        };
    }

    public static void RequireLayerCompatibility(
        string layer,
        string usage,
        string path)
    {
        var expectedUsage = layer switch
        {
            LabelLayerKinds.ClassificationTag =>
                LabelUsages.Classification,
            LabelLayerKinds.FieldStatistic =>
                LabelUsages.Statistic,
            LabelLayerKinds.TableMetric or LabelLayerKinds.RuntimeRow =>
                LabelUsages.TableTarget,
            _ => throw AppExceptionFactory.BadRequest(
                AppErrorCode.LABEL_LAYER_MISMATCH,
                new { path, layer, reason = "UNKNOWN_LAYER" })
        };
        if (!string.Equals(
                expectedUsage,
                usage,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.LABEL_LAYER_MISMATCH,
                new { path, layer, expectedUsage, actualUsage = usage });
        }
    }

    public static LockedLabelSnapshot CreateLockedSnapshot(
        LabelCatalogItem label)
        => new(
            label.Id,
            label.Code,
            label.DataType,
            label.Usage,
            label.ScopeType,
            label.ScopeId,
            label.IsActive,
            label.VersionNo,
            label.VersionId ?? string.Empty,
            label.ConfigHash ?? string.Empty);
}
