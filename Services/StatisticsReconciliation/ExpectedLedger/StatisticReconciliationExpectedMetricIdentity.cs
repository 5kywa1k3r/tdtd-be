using System.Collections.Immutable;
using System.Text;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

public static class StatisticReconciliationExpectedMetricFamilies
{
    public const string Direct = "DIRECT";
    public const string Basic = "BASIC";
    public const string Advanced = "ADVANCED";
    public const string Diff = "DIFF";

    public static readonly ImmutableHashSet<string> All =
        ImmutableHashSet.Create(StringComparer.Ordinal, Direct, Basic, Advanced, Diff);
}

public static class StatisticReconciliationExpectedMetricKinds
{
    public const string Field = "FIELD";
    public const string Table = "TABLE";
    public const string RowLabel = "ROW_LABEL";

    public static readonly ImmutableHashSet<string> All =
        ImmutableHashSet.Create(StringComparer.Ordinal, Field, Table, RowLabel);
}

public static class StatisticReconciliationExpectedBasicScopes
{
    public const string DirectChildrenOrSelf = "DIRECT_CHILDREN_OR_SELF";
    public const string DirectChildren = "DIRECT_CHILDREN";
    public const string FlowBranch = "FLOW_BRANCH";
    public const string FlowStep = "FLOW_STEP";
    public const string FlowEffectivePath = "FLOW_EFFECTIVE_PATH";
    public const string FlowFinal = "FLOW_FINAL";

    public static readonly ImmutableHashSet<string> All =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            DirectChildrenOrSelf,
            DirectChildren,
            FlowBranch,
            FlowStep,
            FlowEffectivePath,
            FlowFinal);
}

public static class StatisticReconciliationExpectedAdvancedGrains
{
    public const string Day = "DAY";
    public const string Month = "MONTH";
    public const string Year = "YEAR";

    public static readonly ImmutableHashSet<string> All =
        ImmutableHashSet.Create(StringComparer.Ordinal, Day, Month, Year);
}

public static class StatisticReconciliationExpectedDiffKinds
{
    public const string Field = "FIELD";
    public const string TableMetric = "TABLE_METRIC";
    public const string RowLabel = "ROW_LABEL";

    public static readonly ImmutableHashSet<string> All =
        ImmutableHashSet.Create(StringComparer.Ordinal, Field, TableMetric, RowLabel);
}

public static class StatisticReconciliationExpectedIdentityFailureReasons
{
    public const string Invalid = "P10_EXPECTED_IDENTITY_INVALID";
    public const string FamilyInvalid = "P10_EXPECTED_IDENTITY_FAMILY_INVALID";
    public const string KindInvalid = "P10_EXPECTED_IDENTITY_KIND_INVALID";
    public const string ScopeInvalid = "P10_EXPECTED_IDENTITY_SCOPE_INVALID";
}

public sealed record ExpectedMetricIdentityRequest
{
    internal ExpectedMetricIdentityRequest(
        string family,
        string kind,
        string metricId,
        string periodKey,
        string? fieldId = null,
        string? tableId = null,
        string? rowId = null,
        string? labelId = null,
        string? scopeKind = null,
        string? scopeId = null,
        string? grain = null,
        string? diffKind = null)
    {
        Family = family;
        Kind = kind;
        MetricId = metricId;
        PeriodKey = periodKey;
        FieldId = fieldId;
        TableId = tableId;
        RowId = rowId;
        LabelId = labelId;
        ScopeKind = scopeKind;
        ScopeId = scopeId;
        Grain = grain;
        DiffKind = diffKind;
    }

    public string Family { get; }
    public string Kind { get; }
    public string MetricId { get; }
    public string PeriodKey { get; }
    public string? FieldId { get; }
    public string? TableId { get; }
    public string? RowId { get; }
    public string? LabelId { get; }
    public string? ScopeKind { get; }
    public string? ScopeId { get; }
    public string? Grain { get; }
    public string? DiffKind { get; }
}

public sealed record StatisticReconciliationExpectedMetricIdentity(
    string SchemaVersion,
    string Family,
    string Kind,
    string MetricId,
    string PeriodKey,
    string? FieldId,
    string? TableId,
    string? RowId,
    string? LabelId,
    string? ScopeKind,
    string? ScopeId,
    string? Grain,
    string? DiffKind,
    string IdentitySha256);

internal interface IStatisticReconciliationExpectedMetricIdentityCompiler
{
    StatisticReconciliationExpectedMetricIdentity Compile(
        ExpectedMetricIdentityRequest request);
}

internal sealed class StatisticReconciliationExpectedMetricIdentityCompiler
    : IStatisticReconciliationExpectedMetricIdentityCompiler
{
    internal const string SchemaVersion = "P10_EXPECTED_METRIC_IDENTITY_V3";

    public StatisticReconciliationExpectedMetricIdentity Compile(
        ExpectedMetricIdentityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var family = Required(request.Family, "$.identity.family");
        var kind = Required(request.Kind, "$.identity.kind");
        var metricId = Required(request.MetricId, "$.identity.metricId");
        var periodKey = Required(request.PeriodKey, "$.identity.periodKey");
        var fieldId = Optional(request.FieldId, "$.identity.fieldId");
        var tableId = Optional(request.TableId, "$.identity.tableId");
        var rowId = Optional(request.RowId, "$.identity.rowId");
        var labelId = Optional(request.LabelId, "$.identity.labelId");
        var scopeKind = Optional(request.ScopeKind, "$.identity.scopeKind");
        var scopeId = Optional(request.ScopeId, "$.identity.scopeId");
        var grain = Optional(request.Grain, "$.identity.grain");
        var diffKind = Optional(request.DiffKind, "$.identity.diffKind");

        if (!StatisticReconciliationExpectedMetricFamilies.All.Contains(family))
            throw Fail(
                StatisticReconciliationExpectedIdentityFailureReasons.FamilyInvalid,
                "$.identity.family",
                "Expected metric family must be Direct, Basic, Advanced or Diff.");
        if (!StatisticReconciliationExpectedMetricKinds.All.Contains(kind))
            throw Fail(
                StatisticReconciliationExpectedIdentityFailureReasons.KindInvalid,
                "$.identity.kind",
                "Expected metric kind must be field, table or row-label.");

        ValidateKind(kind, fieldId, tableId, rowId, labelId);
        switch (family)
        {
            case StatisticReconciliationExpectedMetricFamilies.Direct:
                RequireAbsent(scopeKind, scopeId, grain, diffKind, family);
                break;
            case StatisticReconciliationExpectedMetricFamilies.Basic:
                if (scopeKind is null ||
                    !StatisticReconciliationExpectedBasicScopes.All.Contains(scopeKind) ||
                    scopeId is null || grain is not null ||
                    diffKind is not null)
                    throw Scope(family);
                break;
            case StatisticReconciliationExpectedMetricFamilies.Advanced:
                if (grain is null ||
                    !StatisticReconciliationExpectedAdvancedGrains.All.Contains(grain) ||
                    scopeKind is not null || scopeId is not null ||
                    diffKind is not null)
                    throw Scope(family);
                break;
            case StatisticReconciliationExpectedMetricFamilies.Diff:
                if (diffKind is null ||
                    !StatisticReconciliationExpectedDiffKinds.All.Contains(diffKind) ||
                    scopeKind is not null || scopeId is not null ||
                    grain is not null ||
                    !DiffKindMatchesMetricKind(diffKind, kind))
                    throw Scope(family);
                break;
        }

        var identitySha256 = StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            SchemaVersion,
            [
                family,
                kind,
                metricId,
                periodKey,
                fieldId ?? "~",
                tableId ?? "~",
                rowId ?? "~",
                labelId ?? "~",
                scopeKind ?? "~",
                scopeId ?? "~",
                grain ?? "~",
                diffKind ?? "~"
            ]);
        return new StatisticReconciliationExpectedMetricIdentity(
            SchemaVersion,
            family,
            kind,
            metricId,
            periodKey,
            fieldId,
            tableId,
            rowId,
            labelId,
            scopeKind,
            scopeId,
            grain,
            diffKind,
            identitySha256);
    }

    private static void ValidateKind(
        string kind,
        string? fieldId,
        string? tableId,
        string? rowId,
        string? labelId)
    {
        var valid = kind switch
        {
            StatisticReconciliationExpectedMetricKinds.Field =>
                fieldId is not null &&
                tableId is null && rowId is null && labelId is null,
            StatisticReconciliationExpectedMetricKinds.Table =>
                fieldId is null && tableId is not null &&
                rowId is null && labelId is null,
            StatisticReconciliationExpectedMetricKinds.RowLabel =>
                fieldId is null && tableId is not null &&
                rowId is not null && labelId is not null,
            _ => false
        };
        if (!valid)
            throw Fail(
                StatisticReconciliationExpectedIdentityFailureReasons.KindInvalid,
                "$.identity",
                "Metric kind identifiers are incomplete or mixed.");
    }

    private static bool DiffKindMatchesMetricKind(string diffKind, string kind)
        => (diffKind, kind) switch
        {
            (StatisticReconciliationExpectedDiffKinds.Field,
                StatisticReconciliationExpectedMetricKinds.Field) => true,
            (StatisticReconciliationExpectedDiffKinds.TableMetric,
                StatisticReconciliationExpectedMetricKinds.Table) => true,
            (StatisticReconciliationExpectedDiffKinds.RowLabel,
                StatisticReconciliationExpectedMetricKinds.RowLabel) => true,
            _ => false
        };

    private static void RequireAbsent(
        string? scopeKind,
        string? scopeId,
        string? grain,
        string? diffKind,
        string family)
    {
        if (scopeKind is not null || scopeId is not null ||
            grain is not null || diffKind is not null)
            throw Scope(family);
    }

    private static StatisticReconciliationExpectedLedgerInputException Scope(string family)
        => Fail(
            StatisticReconciliationExpectedIdentityFailureReasons.ScopeInvalid,
            "$.identity",
            $"Identity scope does not match {family}.");

    private static string Required(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 512 ||
            value != value.Trim() ||
            !value.IsNormalized(NormalizationForm.FormC) ||
            value.Any(character => char.IsControl(character) || character == '\u001f'))
            throw Fail(
                StatisticReconciliationExpectedIdentityFailureReasons.Invalid,
                path,
                "Canonical bounded identity token required.");
        return value;
    }

    private static string? Optional(string? value, string path)
        => value is null ? null : Required(value, path);

    private static StatisticReconciliationExpectedLedgerInputException Fail(
        string reason,
        string path,
        string message)
        => new(reason, path, message);
}
