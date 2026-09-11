using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal static partial class Tests
{
    internal static Task DynamicFormExpectedOwnerIsExact()
    {
        var fixture = DynamicFormExpectedFixture.Create();
        var candidate =
            StatisticReconciliationExpectedDynamicFormConfigurationAdapter
                .TryBuildCandidate(fixture.Owner, fixture.Run);

        True(candidate is not null, "exact DYNAMIC_FORM candidate");
        Equal(
            StatisticReconciliationExpectedLedgerConfigurationKinds.DynamicForm,
            candidate!.Kind,
            "DYNAMIC_FORM owner kind");
        Equal(fixture.Owner.Id, candidate.OwnerId, "owner id");
        Equal(fixture.Owner.StatisticConfigId, candidate.ConfigId, "config id");
        using var configuration = JsonDocument.Parse(
            candidate.ConfigurationJson);
        var metric = configuration.RootElement
            .GetProperty("expectedMetrics")[0];
        Equal("DIRECT", metric.GetProperty("family").GetString(), "family");
        Equal("FIELD", metric.GetProperty("kind").GetString(), "kind");
        Equal("amount", metric.GetProperty("metricId").GetString(), "metric id");
        Equal(
            "/directFieldValues/field_amount",
            metric.GetProperty("jsonPointer").GetString(),
            "raw field pointer");
        return Task.CompletedTask;
    }

    internal static Task DynamicFormExpectedOwnerMissingFailsClosed()
    {
        ExpectExactOwnerFailure([]);
        return Task.CompletedTask;
    }

    internal static Task DynamicFormExpectedOwnerAmbiguousFailsClosed()
    {
        var fixture = DynamicFormExpectedFixture.Create();
        var candidate =
            StatisticReconciliationExpectedDynamicFormConfigurationAdapter
                .TryBuildCandidate(fixture.Owner, fixture.Run)!;
        ExpectExactOwnerFailure([candidate, candidate]);
        return Task.CompletedTask;
    }

    internal static Task DynamicFormExpectedOwnerHashDriftFailsClosed()
    {
        var fixture = DynamicFormExpectedFixture.Create();
        var drift = new string('a', 64);
        fixture.Owner.StatisticConfigHash = drift;
        fixture.Owner.StatisticConfigSnapshots[0].ConfigHash = drift;

        try
        {
            _ = StatisticReconciliationExpectedDynamicFormConfigurationAdapter
                .TryBuildCandidate(fixture.Owner, fixture.Run);
        }
        catch (AppException error)
        {
            Equal(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                error.Code,
                "hash drift code");
            True(
                JsonSerializer.Serialize(error.Details)
                    .Contains("DYNAMIC_FORM_STATISTIC_CONFIG_HASH_INTEGRITY",
                        StringComparison.Ordinal),
                "hash drift reason");
            return Task.CompletedTask;
        }

        throw new InvalidOperationException(
            "Expected trusted P804 hash drift to fail closed.");
    }

    internal static Task DynamicFormDirectFieldScopeKeepsFullExpectedPlan()
    {
        var fixture = DynamicFormExpectedFixture.Create(includeSecondField: true);
        fixture.BindDirectFieldScope(
            new
            {
                periodInstanceKey = "MONTH:2026-08",
                fieldId = "field_amount",
                fieldKey = "amount",
                bucketKey = (string?)null,
                periodKey = "2026-08"
            });
        var candidate =
            StatisticReconciliationExpectedDynamicFormConfigurationAdapter
                .TryBuildCandidate(fixture.Owner, fixture.Run);

        True(candidate is not null, "scoped DYNAMIC_FORM candidate");
        using var configuration = JsonDocument.Parse(
            candidate!.ConfigurationJson);
        var metrics = configuration.RootElement
            .GetProperty("expectedMetrics")
            .EnumerateArray()
            .ToArray();
        Equal(2, metrics.Length, "full expected metric plan count");
        True(metrics.Any(metric =>
                metric.GetProperty("fieldId").GetString() == "field_amount" &&
                metric.GetProperty("metricId").GetString() == "amount") &&
            metrics.Any(metric =>
                metric.GetProperty("fieldId").GetString() == "field_units" &&
                metric.GetProperty("metricId").GetString() == "units"),
            "DIRECT_FIELD run preserves full P8 expected ledger");
        return Task.CompletedTask;
    }

    internal static Task DynamicFormLegacyDirectFieldBroadScopeKeepsFullPlan()
    {
        var fixture = DynamicFormExpectedFixture.Create(includeSecondField: true);
        fixture.BindLegacyDirectFieldScope(
            new
            {
                periodInstanceKey = "MONTH:2026-08"
            });
        var candidate =
            StatisticReconciliationExpectedDynamicFormConfigurationAdapter
                .TryBuildCandidate(fixture.Owner, fixture.Run);

        True(candidate is not null, "legacy broad DYNAMIC_FORM candidate");
        using var configuration = JsonDocument.Parse(
            candidate!.ConfigurationJson);
        Equal(
            2,
            configuration.RootElement.GetProperty("expectedMetrics")
                .GetArrayLength(),
            "V3 broad DIRECT_FIELD preserves full expected metric plan");
        return Task.CompletedTask;
    }

    internal static Task DynamicFormDirectFieldScopeFailsClosed()
    {
        var broadV4 = DynamicFormExpectedFixture.Create(
            includeSecondField: true);
        broadV4.BindDirectFieldScope(
            new
            {
                periodInstanceKey = "MONTH:2026-08"
            });
        ExpectDirectFieldScopeFailure(broadV4);

        var absent = DynamicFormExpectedFixture.Create();
        absent.BindDirectFieldScope(
            new
            {
                periodInstanceKey = "MONTH:2026-08",
                fieldId = "field_amount",
                bucketKey = (string?)null,
                periodKey = "2026-08"
            });
        ExpectDirectFieldScopeFailure(absent);

        var mismatch = DynamicFormExpectedFixture.Create();
        mismatch.BindDirectFieldScope(
            new
            {
                periodInstanceKey = "MONTH:2026-08",
                fieldId = "field_amount",
                fieldKey = "amount",
                bucketKey = (string?)null,
                periodKey = "2026-08"
            },
            conceptKey: "other");
        ExpectDirectFieldScopeFailure(mismatch);

        var malformed = DynamicFormExpectedFixture.Create();
        malformed.BindDirectFieldScope(
            new
            {
                periodInstanceKey = "MONTH:2026-08",
                fieldId = "field_amount",
                fieldKey = "amount",
                bucketKey = (string?)null,
                periodKey = "2026-08"
            });
        malformed.Run.FilterHash = new string('a', 64);
        ExpectDirectFieldScopeFailure(malformed);

        var missing = DynamicFormExpectedFixture.Create();
        missing.BindDirectFieldScope(
            new
            {
                periodInstanceKey = "MONTH:2026-08",
                fieldId = "field_missing",
                fieldKey = "amount",
                bucketKey = (string?)null,
                periodKey = "2026-08"
            });
        ExpectDirectFieldScopeFailure(missing);

        var ambiguous = DynamicFormExpectedFixture.Create(
            duplicateSelectedField: true);
        ambiguous.BindDirectFieldScope(
            new
            {
                periodInstanceKey = "MONTH:2026-08",
                fieldId = "field_amount",
                fieldKey = "amount",
                bucketKey = (string?)null,
                periodKey = "2026-08"
            });
        ExpectDirectFieldScopeFailure(ambiguous);
        return Task.CompletedTask;
    }

    internal static Task DynamicFormDirectSumAndMeanCompileFromRawPayload()
    {
        var fixture = DynamicFormExpectedFixture.Create();
        var candidate =
            StatisticReconciliationExpectedDynamicFormConfigurationAdapter
                .TryBuildCandidate(fixture.Owner, fixture.Run)!;
        var payload =
            StatisticReconciliationExpectedDynamicFormConfigurationAdapter
                .ProjectApprovedRawPayload(
                    """
                    {
                      "values1D": [],
                      "fieldValues": {
                        "sourceProvenance": { "field_amount": { "source": "P7" } },
                        "values": { "field_amount": 10 }
                      },
                      "tableValues": { "blocks": [] },
                      "summarySource": {}
                    }
                    """);
        Equal(
            payload,
            StatisticReconciliationExpectedDynamicFormConfigurationAdapter
                .ProjectApprovedRawPayload(payload),
            "approved raw projection is idempotent");
        using (var raw = JsonDocument.Parse(payload))
        {
            Equal(
                10m,
                raw.RootElement.GetProperty("directFieldValues")
                    .GetProperty("field_amount").GetDecimal(),
                "approved raw field projection");
            True(
                raw.RootElement.GetProperty("fieldValues")
                    .TryGetProperty("sourceProvenance", out _),
                "raw envelope remains bound");
        }

        var plan = Fixture.Plan(
            candidate.ConfigurationJson,
            [Fixture.Source("s1", payload)],
            [Fixture.Contribution(
                "s1",
                StatisticReconciliationExpectedContributionPolicies.Include)]);
        var generation = new StatisticReconciliationExpectedTypedCompiler(
                new StatisticReconciliationExpectedMetricIdentityCompiler())
            .Compile(plan, Fixture.CatalogPins());

        var sum = generation.Atoms.Single(atom =>
            atom.Identity.Family ==
            StatisticReconciliationExpectedMetricFamilies.Direct &&
            atom.Identity.Kind ==
            StatisticReconciliationExpectedMetricKinds.Field &&
            atom.Identity.MetricId == "amount" &&
            atom.Identity.FieldId == "field_amount" &&
            atom.AtomKind == StatisticReconciliationExpectedAtomKinds.Sum);
        var mean = generation.Atoms.Single(atom =>
            atom.Identity.MetricId == "amount" &&
            atom.AtomKind == StatisticReconciliationExpectedAtomKinds.Mean);
        Equal("10", sum.CanonicalValue, "DIRECT SUM");
        Equal("10", mean.CanonicalValue, "DIRECT MEAN");
        Equal(1L, sum.ReportCount, "DIRECT report count");
        Equal(1L, sum.NumericValueCount, "DIRECT numeric count");
        return Task.CompletedTask;
    }

    private static void ExpectDirectFieldScopeFailure(
        DynamicFormExpectedFixture fixture)
    {
        try
        {
            _ = StatisticReconciliationExpectedDynamicFormConfigurationAdapter
                .TryBuildCandidate(fixture.Owner, fixture.Run);
        }
        catch (StatisticReconciliationExpectedLedgerInputException error)
        {
            Equal(
                StatisticReconciliationExpectedSourcePlanningFailureReasons
                    .SnapshotInvalid,
                error.Reason,
                "DIRECT_FIELD scope failure class");
            Equal(
                StatisticReconciliationExpectedDynamicFormConfigurationAdapter
                    .DirectFieldScopeNotExactReason,
                error.Message,
                "DIRECT_FIELD scope failure reason");
            return;
        }

        throw new InvalidOperationException(
            "Expected DIRECT_FIELD scope binding to fail closed.");
    }

    private static void ExpectExactOwnerFailure(
        IEnumerable<StatisticReconciliationExpectedLockedP8OwnerCandidate>
            candidates)
    {
        try
        {
            _ = StatisticReconciliationExpectedDynamicFormConfigurationAdapter
                .RequireExact(candidates);
        }
        catch (StatisticReconciliationExpectedLedgerInputException error)
        {
            Equal(
                StatisticReconciliationExpectedSourcePlanningFailureReasons
                    .SnapshotInvalid,
                error.Reason,
                "owner exact failure class");
            Equal(
                StatisticReconciliationExpectedDynamicFormConfigurationAdapter
                    .OwnerNotExactReason,
                error.Message,
                "owner exact failure reason");
            return;
        }

        throw new InvalidOperationException(
            "Expected exact DYNAMIC_FORM owner selection to fail closed.");
    }
}

internal sealed record DynamicFormExpectedFixture(
    DynamicFormTemplate Owner,
    StatisticReconciliationRun Run)
{
    private const string FormId = "64f000000000000000000001";
    private const string ConfigId = "64f000000000000000000002";
    private const string ConfigVersionId = "64f000000000000000000003";
    private const string LabelId = "64f000000000000000000004";
    private const string LabelVersionId = "64f000000000000000000005";
    private const string UnitsLabelId = "64f000000000000000000006";
    private const string UnitsLabelVersionId = "64f000000000000000000007";

    internal static DynamicFormExpectedFixture Create(
        bool includeSecondField = false,
        bool duplicateSelectedField = false)
    {
        var schemaField = new JsonObject
        {
            ["id"] = "field_amount",
            ["sectionId"] = "section_main",
            ["key"] = "amount",
            ["name"] = "Amount",
            ["type"] = "number",
            ["isStatistic"] = true,
            ["statisticLabelCodes"] = new JsonArray("amount"),
            ["statistic"] = new JsonObject
            {
                ["aggregateOps"] = new JsonArray(
                    "COUNT", "SUM", "AVG", "MIN", "MAX"),
                ["bucketMode"] = "NONE",
                ["showInDetail"] = true,
                ["showInTree"] = true
            }
        };
        var schemaFields = new JsonArray(schemaField.DeepClone());
        var sectionsJson = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "section_main",
                ["title"] = "Main",
                ["description"] = null,
                ["tagCodes"] = new JsonArray(),
                ["order"] = 0
            }
        }.ToJsonString();
        var structure = (JsonObject)schemaField.DeepClone();
        structure.Remove("isStatistic");
        structure.Remove("statisticLabelCodes");
        structure.Remove("statistic");
        var structureHash = StatConfigCanonicalJson.HashUtf8(
            CanonicalizeNode(structure));
        var labelHash = Fixture.Sha("dynamic-form-label");
        var label = new DynamicFormStatisticLabelSnapshotDto(
            LabelId,
            "amount",
            "NUMBER",
            "STATISTIC",
            "GLOBAL",
            null,
            true,
            1,
            LabelVersionId,
            labelHash);
        var field = new DynamicFormStatisticFieldConfigDto(
            "field_amount",
            DynamicFormStatisticFieldTypes.Number,
            true,
            new DynamicFormStatisticSettingsPayload(
                ["COUNT", "SUM", "AVG", "MIN", "MAX"],
                "NONE",
                true,
                true),
            ["amount"],
            [label],
            structureHash);
        var fields = new List<DynamicFormStatisticFieldConfigDto> { field };
        var dependencyPins = new List<string>
        {
            $"LABEL:{LabelId}:{LabelVersionId}:1:{labelHash}"
        };
        if (includeSecondField)
        {
            var unitsSchemaField = (JsonObject)schemaField.DeepClone();
            unitsSchemaField["id"] = "field_units";
            unitsSchemaField["key"] = "units";
            unitsSchemaField["name"] = "Units";
            unitsSchemaField["statisticLabelCodes"] =
                new JsonArray("units");
            schemaFields.Add(unitsSchemaField.DeepClone());

            var unitsStructure = (JsonObject)unitsSchemaField.DeepClone();
            unitsStructure.Remove("isStatistic");
            unitsStructure.Remove("statisticLabelCodes");
            unitsStructure.Remove("statistic");
            var unitsStructureHash = StatConfigCanonicalJson.HashUtf8(
                CanonicalizeNode(unitsStructure));
            var unitsLabelHash = Fixture.Sha("dynamic-form-units-label");
            var unitsLabel = new DynamicFormStatisticLabelSnapshotDto(
                UnitsLabelId,
                "units",
                "NUMBER",
                "STATISTIC",
                "GLOBAL",
                null,
                true,
                1,
                UnitsLabelVersionId,
                unitsLabelHash);
            fields.Add(new DynamicFormStatisticFieldConfigDto(
                "field_units",
                DynamicFormStatisticFieldTypes.Number,
                true,
                new DynamicFormStatisticSettingsPayload(
                    ["COUNT", "SUM", "AVG", "MIN", "MAX"],
                    "NONE",
                    true,
                    true),
                ["units"],
                [unitsLabel],
                unitsStructureHash));
            dependencyPins.Add(
                $"LABEL:{UnitsLabelId}:{UnitsLabelVersionId}:1:{unitsLabelHash}");
        }
        if (duplicateSelectedField)
            fields.Add(field);

        var fieldsJson = schemaFields.ToJsonString();
        var fieldSection = StatConfigCanonicalJson.Canonicalize(fields);
        const string tableSection = "[]";
        dependencyPins = dependencyPins
            .Distinct(StringComparer.Ordinal)
            .OrderBy(pin => pin, StringComparer.Ordinal)
            .ToList();
        var configHash = ComputeConfigHash(
            fieldSection,
            tableSection,
            dependencyPins);

        var owner = new DynamicFormTemplate
        {
            Id = FormId,
            Code = "direct-form",
            Name = "Direct form",
            CreatedByUsername = "fixture",
            SchemaVersion = 1,
            VersionNo = 1,
            Revision = 1,
            IsActive = true,
            IsPublished = true,
            SectionsJson = sectionsJson,
            FieldsJson = fieldsJson,
            BlocksJson = "[]",
            StatisticConfigId = ConfigId,
            StatisticConfigVersionId = ConfigVersionId,
            StatisticConfigVersionNo = 1,
            StatisticConfigRevision = 1,
            StatisticConfigStatus = "LOCKED",
            StatisticConfigHash = configHash,
            StatisticConfigDependencyPins = dependencyPins,
            StatisticConfigSections = new DynamicFormStatisticConfigSections
            {
                FieldSectionJson = fieldSection,
                TableSectionJson = tableSection
            },
            StatisticConfigSnapshots =
            [
                new DynamicFormStatisticConfigVersionSnapshot
                {
                    VersionId = ConfigVersionId,
                    VersionNo = 1,
                    Revision = 1,
                    Status = "LOCKED",
                    ConfigHash = configHash,
                    DependencyPins = dependencyPins.ToList(),
                    Sections = new DynamicFormStatisticConfigSections
                    {
                        FieldSectionJson = fieldSection,
                        TableSectionJson = tableSection
                    },
                    CreatedAtUtc = Fixture.UtcNow
                }
            ],
            IsDeleted = false
        };
        var published = DynamicFormPublishedSchemaSnapshotBuilder.Build(owner);
        owner.PublishedSchemaSnapshotJson = published.Json;
        owner.PublishedSchemaHash = published.Sha256;

        var run = new StatisticReconciliationRun
        {
            P9ResultKind = StatisticReconciliationP9ResultKinds.Direct,
            DynamicFormVersionId = FormId,
            P8ConfigOwnerId = FormId,
            P8ConfigId = ConfigId,
            P8ConfigVersionId = ConfigVersionId,
            P8ConfigVersionNo = 1,
            P8ConfigRevision = 1,
            P8ConfigHash = configHash
        };
        return new DynamicFormExpectedFixture(owner, run);
    }

    internal void BindDirectFieldScope(
        object filter,
        string conceptKey = "amount")
        => BindDirectFieldScope(
            filter,
            conceptKey,
            StatisticReconciliationActualCapturePlanVersions.V4);

    internal void BindLegacyDirectFieldScope(
        object filter,
        string conceptKey = "amount")
        => BindDirectFieldScope(
            filter,
            conceptKey,
            StatisticReconciliationActualCapturePlanVersions.V3);

    private void BindDirectFieldScope(
        object filter,
        string conceptKey,
        string schemaVersion)
    {
        var element = JsonSerializer.SerializeToElement(filter);
        var canonical = StatisticReconciliationCanonicalJson.Canonicalize(
            element);
        Run.ActualCapturePlan = new StatisticReconciliationActualCapturePlan
        {
            SchemaVersion = schemaVersion,
            Api = new StatisticReconciliationActualApiTargetPlan
            {
                Surface = "DIRECT_FIELD"
            }
        };
        Run.CanonicalFilterJson = canonical;
        Run.FilterHash = StatisticReconciliationCanonicalJson.HashText(
            canonical);
        Run.PeriodInstanceKey = "MONTH:2026-08";
        Run.ConceptKey = conceptKey;
    }

    private static string ComputeConfigHash(
        string fieldSectionJson,
        string tableSectionJson,
        IReadOnlyList<string> dependencyPins)
    {
        using var fields = JsonDocument.Parse(fieldSectionJson);
        using var tables = JsonDocument.Parse(tableSectionJson);
        return StatConfigCanonicalJson.HashObject(new
        {
            ownerKind = StatConfigOwnerKinds.DynamicForm,
            ownerId = FormId,
            fieldConfig = fields.RootElement.Clone(),
            tableConfig = tables.RootElement.Clone(),
            dependencyPins
        });
    }

    private static string CanonicalizeNode(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return StatConfigCanonicalJson.CanonicalizeElement(
            document.RootElement);
    }
}
