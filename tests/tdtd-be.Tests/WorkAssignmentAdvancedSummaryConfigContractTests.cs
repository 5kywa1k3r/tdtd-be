using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;

internal static class WorkAssignmentAdvancedSummaryConfigContractTests
{
    private const string SectionId = "section-advanced";
    private const string FlowInstanceId =
        "abcdef000000000000000001";
    private const string FlowBranchId =
        "abcdef000000000000000002";

    public static void Run()
    {
        StrictPayloadShapeRejectsUnknownFields();
        SourceScopeUsesExactSevenModeContract();
        TypedOperationMatrixIsExact();
        SectionTargetAndHierarchyLimitsAreExact();
        GroupingAndOrderingAreStrictAndOrdered();
        CanonicalUtf8PayloadBoundaryIsExact();
        ValidationReceiptIdentityAndLimitsAreDeterministic();
    }

    private static void StrictPayloadShapeRejectsUnknownFields()
    {
        var json =
            "{\"commandId\":\"advanced-put-1\"," +
            "\"expectedRevision\":0," +
            "\"expectedConfigHash\":\"" +
            StatConfigCanonicalJson.EmptyConfigHash +
            "\",\"payload\":{" +
            "\"sourceScope\":{\"mode\":\"SELF\"," +
            "\"flowInstanceId\":null,\"flowStepId\":null," +
            "\"flowBranchId\":null,\"flowEffectiveStatus\":null}," +
            "\"sections\":[{\"sectionId\":\"" + SectionId +
            "\",\"isCumulative\":false,\"targets\":[]," +
            "\"legacyOperation\":true}]," +
            "\"hierarchyGrains\":[],\"grouping\":[]," +
            "\"ordering\":[],\"description\":null}}";
        using var document = JsonDocument.Parse(json);
        ExpectSchema(
            () => StatConfigCanonicalJson.DeserializeStrict<
                StatConfigMutationEnvelope<
                    WorkAssignmentAdvancedSummaryConfigPayload>>(
                    document.RootElement),
            "unknown section property");

        AssertPropertyNames(
            typeof(WorkAssignmentAdvancedSummaryConfigPayload),
            [
                "SourceScope",
                "Sections",
                "HierarchyGrains",
                "Grouping",
                "Ordering",
                "Description"
            ]);
        AssertPropertyNames(
            typeof(WorkAssignmentAdvancedSummaryTargetPayload),
            ["FieldId", "DataType", "Operation"]);
        AssertPropertyNames(
            typeof(WorkAssignmentAdvancedSummaryOrderingPayload),
            ["FieldId", "Direction"]);
        AssertPropertyNames(
            typeof(WorkAssignmentAdvancedSummaryEmptyCommandPayload),
            []);
    }

    private static void SourceScopeUsesExactSevenModeContract()
    {
        var expected = new[]
        {
            "DIRECT_CHILDREN_OR_SELF",
            "DIRECT_CHILDREN",
            "SELF",
            "FLOW_BRANCH",
            "FLOW_STEP",
            "FLOW_EFFECTIVE_PATH",
            "FLOW_FINAL"
        };
        AssertSequenceEqual(
            expected,
            WorkAssignmentAdvancedSummaryConfigContract
                .SourceScopeModes,
            "source-scope catalog");

        foreach (var mode in expected)
        {
            var source = mode switch
            {
                "FLOW_BRANCH" =>
                    new WorkAssignmentAdvancedSummarySourceScopePayload(
                        mode,
                        FlowInstanceId,
                        null,
                        FlowBranchId,
                        "EFFECTIVE"),
                "FLOW_STEP" =>
                    new WorkAssignmentAdvancedSummarySourceScopePayload(
                        mode,
                        FlowInstanceId,
                        "step-1",
                        null,
                        "ANY"),
                "FLOW_EFFECTIVE_PATH" or "FLOW_FINAL" =>
                    new WorkAssignmentAdvancedSummarySourceScopePayload(
                        mode,
                        FlowInstanceId,
                        null,
                        null,
                        "TERMINATED"),
                _ =>
                    new WorkAssignmentAdvancedSummarySourceScopePayload(
                        mode,
                        null,
                        null,
                        null,
                        null)
            };
            var normalized = Normalize(Payload(sourceScope: source));
            AssertEqual(
                mode,
                normalized.SourceScope!.Mode,
                $"source mode {mode}");
        }

        ExpectSchema(
            () => Normalize(
                Payload(
                    sourceScope:
                        new WorkAssignmentAdvancedSummarySourceScopePayload(
                            "DESCENDANTS",
                            null,
                            null,
                            null,
                            null))),
            "unsupported source mode");
        ExpectSchema(
            () => Normalize(
                Payload(
                    sourceScope:
                        new WorkAssignmentAdvancedSummarySourceScopePayload(
                            "SELF",
                            FlowInstanceId,
                            null,
                            null,
                            null))),
            "non-flow conditional field");
        ExpectSchema(
            () => Normalize(
                Payload(
                    sourceScope:
                        new WorkAssignmentAdvancedSummarySourceScopePayload(
                            "FLOW_BRANCH",
                            FlowInstanceId.ToUpperInvariant(),
                            null,
                            FlowBranchId,
                            "EFFECTIVE"))),
            "non-canonical flow ObjectId");
    }

    private static void TypedOperationMatrixIsExact()
    {
        var matrix =
            new Dictionary<string, string[]>(
                StringComparer.Ordinal)
            {
                ["NUMBER"] =
                    ["SUM", "MIN", "MAX", "MEAN", "COUNT"],
                ["DATE"] = ["MIN_DATE", "MAX_DATE", "COUNT"],
                ["BOOLEAN"] =
                    ["TRUE_COUNT", "FALSE_COUNT", "COUNT"],
                ["CHOICE"] = ["BUCKET_COUNT", "COUNT"],
                ["TEXT"] = ["JOIN", "COUNT"]
            };
        var everyOperation = matrix.Values
            .SelectMany(value => value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var (dataType, allowed) in matrix)
        {
            foreach (var operation in allowed)
            {
                var normalized = Normalize(
                    Payload(
                        targets:
                        [
                            new(
                                "field-1",
                                dataType,
                                operation)
                        ]));
                AssertEqual(
                    operation,
                    normalized.Sections![0]
                        .Targets![0].Operation,
                    $"allowed {dataType}/{operation}");
            }
            foreach (var incompatible in everyOperation.Except(
                         allowed,
                         StringComparer.Ordinal))
            {
                ExpectSchema(
                    () => Normalize(
                        Payload(
                            targets:
                            [
                                new(
                                    "field-1",
                                    dataType,
                                    incompatible)
                            ])),
                    $"incompatible {dataType}/{incompatible}");
            }
        }
        foreach (var alias in new[]
                 {
                     "MINIMUM",
                     "MAXIMUM",
                     "MINIMUM_DATE",
                     "MAXIMUM_DATE",
                     "AVG"
                 })
        {
            ExpectSchema(
                () => Normalize(
                    Payload(
                        targets:
                        [
                            new(
                                "field-1",
                                alias.Contains("DATE")
                                    ? "DATE"
                                    : "NUMBER",
                                alias)
                        ])),
                $"operation alias {alias}");
        }
    }

    private static void SectionTargetAndHierarchyLimitsAreExact()
    {
        _ = Normalize(Payload(isCumulative: true, targetCount: 249));
        ExpectSchema(
            () => Normalize(
                Payload(isCumulative: true, targetCount: 250)),
            "cumulative 250");
        _ = Normalize(Payload(isCumulative: false, targetCount: 1000));
        ExpectSchema(
            () => Normalize(
                Payload(
                    isCumulative: false,
                    targetCount: 1001)),
            "non-cumulative 1001");

        var single = Payload();
        ExpectSchema(
            () => Normalize(
                single with
                {
                    Sections =
                    [
                        single.Sections![0],
                        single.Sections![0]
                    ]
                }),
            "two sections");
        ExpectSchema(
            () => Normalize(
                single with
                {
                    Sections =
                    [
                        single.Sections![0] with
                        {
                            SectionId = "other-section"
                        }
                    ]
                }),
            "section route mismatch");

        _ = Normalize(
            Payload(hierarchy: ["DAY", "MONTH", "YEAR"]));
        ExpectSchema(
            () => Normalize(
                Payload(
                    hierarchy:
                        ["DAY", "MONTH", "YEAR", "DAY"])),
            "hierarchy depth four");
        ExpectSchema(
            () => Normalize(
                Payload(hierarchy: ["DAY", "YEAR"])),
            "hierarchy non-prefix");
    }

    private static void GroupingAndOrderingAreStrictAndOrdered()
    {
        var normalized = Normalize(
            Payload(
                grouping: ["PERIOD", "UNIT"],
                targets:
                [
                    new("field-1", "NUMBER", "SUM"),
                    new("field-2", "TEXT", "COUNT")
                ],
                ordering:
                [
                    new("field-2", "DESC"),
                    new("field-1", "ASC")
                ]));
        AssertSequenceEqual(
            new[] { "PERIOD", "UNIT" },
            normalized.Grouping!,
            "grouping preserves order");
        AssertSequenceEqual(
            new[] { "field-2", "field-1" },
            normalized.Ordering!
                .Select(item => item.FieldId!)
                .ToArray(),
            "ordering preserves order");

        ExpectSchema(
            () => Normalize(
                Payload(grouping: ["UNIT", "UNIT"])),
            "duplicate grouping");
        ExpectSchema(
            () => Normalize(
                Payload(
                    ordering:
                    [
                        new("missing-field", "ASC")
                    ])),
            "ordering must reference target");
        ExpectSchema(
            () => Normalize(
                Payload(
                    ordering:
                    [
                        new("field-0", "ASC"),
                        new("field-0", "DESC")
                    ])),
            "duplicate ordering field");
    }

    private static void CanonicalUtf8PayloadBoundaryIsExact()
    {
        var emptyDescription = Normalize(Payload(description: ""));
        var baseBytes =
            WorkAssignmentAdvancedSummaryConfigService
                .P805CanonicalPayloadBytes(emptyDescription);
        var padding =
            WorkAssignmentAdvancedSummaryConfigContract
                .MaxCanonicalPayloadBytes - baseBytes;
        AssertTrue(padding > 0, "payload padding must be positive");

        var exact = Normalize(
            Payload(description: new string('x', padding)));
        AssertEqual(
            WorkAssignmentAdvancedSummaryConfigContract
                .MaxCanonicalPayloadBytes,
            WorkAssignmentAdvancedSummaryConfigService
                .P805CanonicalPayloadBytes(exact),
            "canonical UTF-8 exact boundary");
        WorkAssignmentAdvancedSummaryConfigService
            .EnsureP805PayloadSize(exact);

        var over = Normalize(
            Payload(description: new string('x', padding + 1)));
        ExpectSchema(
            () => WorkAssignmentAdvancedSummaryConfigService
                .EnsureP805PayloadSize(over),
            "canonical UTF-8 N+1");
    }

    private static void
        ValidationReceiptIdentityAndLimitsAreDeterministic()
    {
        const string ownerId =
            "100000000000000000000010:" +
            "200000000000000000000010:" +
            SectionId;
        const string versionId =
            "300000000000000000000010";
        const long revision = 7;
        var configHash = new string('a', 64);
        var expected = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        $"ADVANCED_SUMMARY_VALIDATION\0" +
                        $"{ownerId}\0{versionId}\0" +
                        $"{revision}\0{configHash}")))
            .ToLowerInvariant();
        AssertEqual(
            expected,
            WorkAssignmentAdvancedSummaryConfigService
                .P805ValidationReceiptId(
                    ownerId,
                    versionId,
                    revision,
                    configHash),
            "validation receipt id");
        AssertPropertyNames(
            typeof(
                WorkAssignmentAdvancedSummaryValidationReceipt),
            [
                "ReceiptId",
                "ContractVersion",
                "ValidationMode",
                "OwnerId",
                "ConfigId",
                "VersionId",
                "VersionNo",
                "Revision",
                "ConfigHash",
                "DependencyPins",
                "SectionId",
                "SourceScopeMode",
                "IsCumulative",
                "TargetCount",
                "TargetLimit",
                "HierarchyGrains",
                "HierarchyDepth",
                "MaxHierarchyDepth",
                "CanonicalPayloadBytes",
                "MaxCanonicalPayloadBytes",
                "RuntimeEligibility",
                "PreviewRead",
                "PreviewWrite",
                "HierarchyRead",
                "HierarchyWrite"
            ]);
    }

    private static WorkAssignmentAdvancedSummaryConfigPayload
        Normalize(
            WorkAssignmentAdvancedSummaryConfigPayload payload)
        => WorkAssignmentAdvancedSummaryConfigService
            .NormalizeP805Payload(payload, SectionId);

    private static WorkAssignmentAdvancedSummaryConfigPayload Payload(
        WorkAssignmentAdvancedSummarySourceScopePayload? sourceScope = null,
        bool isCumulative = false,
        int? targetCount = null,
        IReadOnlyList<
            WorkAssignmentAdvancedSummaryTargetPayload>? targets = null,
        IReadOnlyList<string>? hierarchy = null,
        IReadOnlyList<string>? grouping = null,
        IReadOnlyList<
            WorkAssignmentAdvancedSummaryOrderingPayload>? ordering = null,
        string? description = null)
    {
        targets ??= Enumerable.Range(0, targetCount ?? 1)
            .Select(
                index =>
                    new WorkAssignmentAdvancedSummaryTargetPayload(
                        $"field-{index}",
                        "NUMBER",
                        "COUNT"))
            .ToArray();
        return new WorkAssignmentAdvancedSummaryConfigPayload(
            sourceScope ??
            new WorkAssignmentAdvancedSummarySourceScopePayload(
                "DIRECT_CHILDREN_OR_SELF",
                null,
                null,
                null,
                null),
            [
                new WorkAssignmentAdvancedSummarySectionPayload(
                    SectionId,
                    isCumulative,
                    targets)
            ],
            hierarchy ?? Array.Empty<string>(),
            grouping ?? Array.Empty<string>(),
            ordering ??
            Array.Empty<
                WorkAssignmentAdvancedSummaryOrderingPayload>(),
            description);
    }

    private static void ExpectSchema(Action action, string scope)
    {
        try
        {
            action();
        }
        catch (AppException ex) when (
            ex.Code == AppErrorCode.STAT_CONFIG_SCHEMA_INVALID)
        {
            return;
        }
        catch (AppException ex)
        {
            throw new InvalidOperationException(
                $"{scope}: expected schema error, got {ex.Code}.",
                ex);
        }
        throw new InvalidOperationException(
            $"{scope}: expected schema error.");
    }

    private static void AssertPropertyNames(
        Type type,
        IReadOnlyList<string> expected)
        => AssertSequenceEqual(
            expected.OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            type.GetProperties()
                .Select(property => property.Name)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            $"{type.Name} property names");

    private static void AssertTrue(bool value, string scope)
    {
        if (!value)
            throw new InvalidOperationException(scope);
    }

    private static void AssertEqual<T>(
        T expected,
        T actual,
        string scope)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{scope}: expected {expected}, got {actual}.");
        }
    }

    private static void AssertSequenceEqual<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        string scope)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"{scope}: expected [{string.Join(", ", expected)}], " +
                $"got [{string.Join(", ", actual)}].");
        }
    }
}
