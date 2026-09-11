using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string BasicRowLabelCode = "p8.basic.row.label";
    private readonly Dictionary<string, P8BasicFixture> _basicFixtures =
        new(StringComparer.Ordinal);
    private string _basicSourceReportId = default!;
    private P8ConfigIdentity _basicRowLabelSnapshot = default!;
    private P8ConfigIdentity _basicRowLabelLive = default!;
    private P8ConfigIdentity _basicRowLabelDuplicate = default!;
    private P8FieldConfigIdentity _basicModernStatisticConfig = default!;

    private static readonly IReadOnlyDictionary<string, string[]> BasicTargetKeys =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["NUMBER"] =
            [
                "basic_number_sum",
                "basic_number_min",
                "basic_number_max",
                "basic_number_mean",
                "basic_number_count"
            ],
            ["DATE"] =
            [
                "basic_date_min",
                "basic_date_max",
                "basic_date_count"
            ],
            ["BOOLEAN"] =
            [
                "basic_boolean_true",
                "basic_boolean_false",
                "basic_boolean_count"
            ],
            ["CHOICE"] =
            [
                "basic_choice_bucket",
                "basic_choice_count"
            ],
            ["TEXT"] =
            [
                "basic_text_join",
                "basic_text_count"
            ]
        };

    private async Task SeedP804FixturesAsync(CancellationToken ct)
    {
        var actor = Actor("system_admin");
        (_, _basicRowLabelSnapshot) = await CreateLabelAsync(
            actor,
            "p8-bas-setup-row-label-snapshot",
            LabelPayload(
                BasicRowLabelCode,
                "P8 Basic frozen row label",
                "GLOBAL",
                null,
                usage: "TABLE_TARGET",
                dataType: "SHORT_TEXT"),
            ct);

        var form = BuildBasicDynamicFormFixture(
            "P8_BASIC_SUMMARY_FORM",
            "P8 Basic Summary typed concepts",
            includeLegacyRowLabelDeclaration: false);
        var legacyForm = BuildBasicDynamicFormFixture(
            "P8_BASIC_SUMMARY_FORM_LEGACY",
            "P8 Basic Summary legacy row-label ambiguity",
            includeLegacyRowLabelDeclaration: true);
        await _database.GetCollection<DynamicFormTemplate>(DynamicFormsCollection)
            .InsertManyAsync([form, legacyForm], cancellationToken: ct);

        _basicModernStatisticConfig =
            await SeedP804ModernStatisticConfigAsync(
                form,
                _basicRowLabelSnapshot,
                ct);

        (_, _basicRowLabelLive) = await UpdateLabelAsync(
            actor,
            _basicRowLabelSnapshot,
            "p8-bas-setup-row-label-live-drift",
            LabelPayload(
                BasicRowLabelCode,
                "P8 Basic live row label drifted after P8-03 snapshot",
                "GLOBAL",
                null,
                usage: "TABLE_TARGET",
                dataType: "SHORT_TEXT"),
            ct);
        (_, _basicRowLabelDuplicate) = await CreateLabelAsync(
            actor,
            "p8-bas-setup-row-label-cross-scope-duplicate",
            LabelPayload(
                BasicRowLabelCode,
                "P8 Basic cross-scope duplicate",
                "UNIT",
                _unitAId,
                usage: "TABLE_TARGET",
                dataType: "SHORT_TEXT"),
            ct);

        var keys = Enumerable.Range(1, 10)
            .Select(index => $"{index:000}")
            .Concat(["011", "014", "015", "016", "infra"])
            .ToArray();
        var assignments = keys.Select((key, index) =>
                BuildBasicAssignmentFixture(key, index, form))
            .Append(BuildBasicAssignmentFixture(
                "legacy",
                keys.Length,
                legacyForm))
            .ToArray();
        await _database.GetCollection<WorkAssignment>("work_assignments")
            .InsertManyAsync(
                assignments.Select(item => item.Assignment),
                cancellationToken: ct);
        foreach (var fixture in assignments)
            _basicFixtures.Add(fixture.Key, fixture);

        _basicSourceReportId = ObjectId.GenerateNewId().ToString();
        var sourceReport = new BsonDocument
        {
            ["_id"] = ObjectId.Parse(_basicSourceReportId),
            ["workAssignmentId"] = ObjectId.Parse(BasicFixture("016").Assignment.Id),
            ["dynamicFormTemplateId"] = ObjectId.Parse(form.Id),
            ["status"] = 3,
            ["isCurrent"] = true,
            ["isActive"] = true,
            ["isDeleted"] = false,
            ["periodKey"] = "20260802",
            ["payloadRevision"] = 1,
            ["payloadHash"] = Sha256(Encoding.UTF8.GetBytes("{}")),
            ["createdByUserId"] = ObjectId.Parse(_adminId),
            ["updatedByUserId"] = ObjectId.Parse(_adminId),
            ["createdAtUtc"] = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
            ["updatedAtUtc"] = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc)
        };
        await _database.GetCollection<BsonDocument>("work_assignment_reports")
            .InsertOneAsync(sourceReport, cancellationToken: ct);

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-04-fixtures.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                form = new
                {
                    form.Id,
                    form.Code,
                    fieldTargetCount = BasicTargetKeys.Sum(item => item.Value.Length),
                    tableMetricConceptKey = "basic-table:metric:number",
                    rowLabelConceptKey = BasicRowLabelCode,
                    statisticConfig = new
                    {
                        _basicModernStatisticConfig.ConfigId,
                        _basicModernStatisticConfig.VersionId,
                        _basicModernStatisticConfig.VersionNo,
                        _basicModernStatisticConfig.Revision,
                        _basicModernStatisticConfig.ConfigHash,
                        _basicModernStatisticConfig.TableSectionHash
                    }
                },
                legacyForm = new
                {
                    legacyForm.Id,
                    legacyForm.Code,
                    rowLabelConceptKey = BasicRowLabelCode,
                    statisticIdentityAbsent = true
                },
                rowLabel = new
                {
                    frozen = new
                    {
                        _basicRowLabelSnapshot.LabelId,
                        _basicRowLabelSnapshot.VersionId,
                        _basicRowLabelSnapshot.VersionNo,
                        _basicRowLabelSnapshot.ConfigHash,
                        _basicRowLabelSnapshot.LabelScopeType,
                        _basicRowLabelSnapshot.LabelScopeId
                    },
                    liveDrift = new
                    {
                        _basicRowLabelLive.LabelId,
                        _basicRowLabelLive.VersionId,
                        _basicRowLabelLive.VersionNo,
                        _basicRowLabelLive.ConfigHash
                    },
                    crossScopeDuplicate = new
                    {
                        _basicRowLabelDuplicate.LabelId,
                        _basicRowLabelDuplicate.VersionId,
                        _basicRowLabelDuplicate.VersionNo,
                        _basicRowLabelDuplicate.ConfigHash,
                        _basicRowLabelDuplicate.LabelScopeType,
                        _basicRowLabelDuplicate.LabelScopeId
                    },
                    modernSnapshotMustWin = true,
                    legacyAmbiguityMustReject = true
                },
                assignments = assignments.Select(item => new
                {
                    item.Key,
                    item.Assignment.Id,
                    item.Assignment.WorkId,
                    item.FlowInstanceId,
                    item.FlowStepId,
                    item.FlowBranchId
                }),
                existingSourceReportId = _basicSourceReportId,
                fixtureWritesOutsideCaseDeltas = true
            },
            ct);
    }

    private async Task<P8FieldConfigIdentity>
        SeedP804ModernStatisticConfigAsync(
            DynamicFormTemplate form,
            P8ConfigIdentity rowLabel,
            CancellationToken ct)
    {
        var actor = Actor("system_admin");
        var current = await ReadFieldConfigAsync(
            actor,
            form.Id,
            ct,
            requirePersisted: false);
        var response = await _api.PatchAsync(
            $"api/dynamic-forms/{form.Id}/statistics",
            Envelope(
                "p8-bas-setup-modern-table-statistics",
                current.Revision,
                current.ConfigHash,
                TablePayload(TablePatch(
                    "basic-table",
                    "FIXED_GRID",
                    false,
                    [
                        TableMetric(
                            "metric:number",
                            "NUMBER",
                            ["COUNT", "SUM"])
                    ],
                    allowedRowLabelCodes: [rowLabel.LabelCode]))),
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            System.Net.HttpStatusCode.OK,
            "seed P8-04 modern Dynamic Form statistic config");
        var identity = ParseFieldIdentity(
            response.Json,
            requireReceipt: true);
        RequireFieldIdentityContract(identity, form.Id);
        await RequireDirectFieldIdentityAsync(
            identity,
            "p8-bas-setup-modern-table-statistics",
            actor.Id,
            ct);

        var readback = await ReadFieldConfigAsync(
            actor,
            form.Id,
            ct,
            requirePersisted: true);
        HarnessAssert.Equal(
            CanonicalFieldReadback(identity.Raw),
            CanonicalFieldReadback(readback.Raw),
            "P8-04 modern statistic seed differs from stable API readback");
        RequireTableReadback(
            identity,
            "basic-table",
            "FIXED_GRID",
            false,
            "BLOCKS_JSON",
            MetricMap((
                "metric:number",
                "NUMBER",
                ["COUNT", "SUM"])),
            expectedRowLabels: [rowLabel.LabelCode]);
        RequirePinnedRowLabel(
            identity,
            "basic-table",
            rowLabel);
        return identity;
    }

    private DynamicFormTemplate BuildBasicDynamicFormFixture(
        string code,
        string name,
        bool includeLegacyRowLabelDeclaration)
    {
        var formId = ObjectId.GenerateNewId().ToString();
        var fields = new JsonArray();
        var order = 0;
        foreach (var family in BasicTargetKeys)
        {
            foreach (var key in family.Value)
            {
                fields.Add(new JsonObject
                {
                    ["id"] = key,
                    ["sectionId"] = "main",
                    ["key"] = key,
                    ["name"] = $"P8 Basic {key}",
                    ["type"] = family.Key switch
                    {
                        "NUMBER" => "number",
                        "DATE" => "date",
                        "BOOLEAN" => "boolean",
                        "CHOICE" => "singleSelect",
                        _ => "shortText"
                    },
                    ["required"] = false,
                    ["order"] = order++
                });
            }
        }
        var blocks = new JsonArray(new JsonObject
        {
            ["blockId"] = "basic-table",
            ["sectionId"] = "main",
            ["name"] = "P8 Basic table concept",
            ["tableMode"] = "FIXED_GRID",
            ["rowLabelDataType"] = "SHORT_TEXT",
            ["metricRules"] = new JsonArray(new JsonObject
            {
                ["metricKey"] = "metric:number",
                ["dataType"] = "NUMBER",
                ["aggregateOps"] = new JsonArray()
            }),
            ["allowedRowLabelCodes"] = includeLegacyRowLabelDeclaration
                ? new JsonArray(JsonValue.Create(BasicRowLabelCode))
                : new JsonArray(),
            ["metricLabelTargets"] = new JsonArray(),
            ["statisticsDisabled"] = false
        });
        var fixedAt = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
        return new DynamicFormTemplate
        {
            Id = formId,
            Code = code,
            Name = name,
            Description = "P8-04 isolated configuration fixture",
            TagCodes = [],
            CreatedByUsername = Actor("system_admin").Username,
            SchemaVersion = 1,
            VersionNo = 1,
            FamilyId = formId,
            LineageStatus = DynamicFormLineageStatuses.Root,
            Revision = 1,
            IsActive = true,
            IsPublished = false,
            SectionsJson =
                "[{\"id\":\"main\",\"title\":\"P8 Basic\",\"description\":null,\"tagCodes\":[],\"order\":0}]",
            FieldsJson = fields.ToJsonString(),
            BlocksJson = blocks.ToJsonString(),
            ExcelBlockJson = null,
            CreatedByUserId = _adminId,
            UpdatedByUserId = _adminId,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };
    }

    private P8BasicFixture BuildBasicAssignmentFixture(
        string key,
        int index,
        DynamicFormTemplate form)
    {
        var assignmentId = ObjectId.GenerateNewId().ToString();
        var flowInstanceId = ObjectId.GenerateNewId().ToString();
        var flowBranchId = ObjectId.GenerateNewId().ToString();
        var flowStepId = $"p8-basic-step-{key}";
        var fixedAt = new DateTime(2026, 8, 2, 0, 1, 0, DateTimeKind.Utc)
            .AddSeconds(index);
        var actor = Actor("system_admin");
        var assignment = new WorkAssignment
        {
            Id = assignmentId,
            WorkId = ObjectId.GenerateNewId().ToString(),
            DynamicFormTemplateId = form.Id,
            DynamicFormTemplateCode = form.Code,
            DynamicFormTemplateName = form.Name,
            DynamicFormFamilyId = form.FamilyId,
            DynamicFormVersionNo = form.VersionNo,
            WorkType = "P8_BASIC_SUMMARY",
            AssignmentType = "ONCE",
            AggregationType = "NONE",
            Assignees =
            [
                new UserRef
                {
                    UserId = actor.Id,
                    Username = actor.Username,
                    FullName = "P8 System Admin",
                    UnitId = actor.UnitId,
                    UnitSymbol = "ROOT",
                    UnitShortName = "ROOT",
                    UnitName = "P8 Root"
                }
            ],
            LeaderWatcherUserIds = [actor.Id],
            IsActive = true,
            RootAssignmentId = assignmentId,
            Level = 0,
            Code = $"P8-BASIC-{key}",
            Name = $"P8 Basic Summary owner {key}",
            Path = $"/{assignmentId}/",
            FlowInstanceId = flowInstanceId,
            FlowStepId = flowStepId,
            FlowStepCode = $"STEP_{key}",
            FlowStepOrder = index + 1,
            FlowBranchId = flowBranchId,
            FlowAttemptNo = 1,
            FlowExecutionEpoch = 1,
            FlowRole = "SOURCE",
            FlowEffectiveStatus = "EFFECTIVE",
            IsFlowFinalNode = true,
            CreatedByUserId = actor.Id,
            UpdatedByUserId = actor.Id,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };
        return new P8BasicFixture(
            key,
            form.Id,
            assignment,
            flowInstanceId,
            flowStepId,
            flowBranchId);
    }

    private P8BasicFixture BasicFixture(string key)
        => _basicFixtures.TryGetValue(key, out var fixture)
            ? fixture
            : throw new HarnessCaseNotRunnableException(
                $"P8 Basic Summary fixture {key} was not seeded.");
}

internal sealed record P8BasicFixture(
    string Key,
    string DynamicFormTemplateId,
    WorkAssignment Assignment,
    string FlowInstanceId,
    string FlowStepId,
    string FlowBranchId);
