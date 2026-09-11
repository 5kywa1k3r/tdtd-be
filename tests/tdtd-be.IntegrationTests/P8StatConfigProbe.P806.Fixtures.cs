using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string DiffBlockId = "diff-table";
    private const string DiffMetricKey = "metric:number";
    private readonly Dictionary<string, P8DiffFixture> _diffFixtures =
        new(StringComparer.Ordinal);
    private P8TableFixture _diffTableFixture = default!;
    private P8FieldConfigIdentity _diffStatisticConfig = default!;
    private P8ConfigIdentity _p806FieldLabel = default!;
    private P8ConfigIdentity _p806MetricLabel = default!;
    private P8ConfigIdentity _p806RowLabel = default!;

    private async Task SeedP806FixturesAsync(CancellationToken ct)
    {
        await SeedP803LabelsAsync(ct);

        var actor = Actor("system_admin");
        (_, _p806FieldLabel) = await CreateLabelAsync(
            actor,
            "p8-dif-setup-field-label",
            LabelPayload(
                "p8.dif.field.statistic",
                "P8 diff field statistic",
                "GLOBAL",
                null,
                usage: "STATISTIC",
                dataType: "NUMBER"),
            ct);
        (_, _p806MetricLabel) = await CreateLabelAsync(
            actor,
            "p8-dif-setup-metric-label",
            LabelPayload(
                "p8.dif.metric.target",
                "P8 diff metric target",
                "GLOBAL",
                null,
                usage: "TABLE_TARGET",
                dataType: "NUMBER"),
            ct);
        (_, _p806RowLabel) = await CreateLabelAsync(
            actor,
            "p8-dif-setup-row-label",
            LabelPayload(
                "p8.dif.row.target",
                "P8 diff row target",
                "GLOBAL",
                null,
                usage: "TABLE_TARGET",
                dataType: "NUMBER"),
            ct);

        _diffTableFixture = NewTableFixture(
            "diff",
            [
                TableBlock(
                    DiffBlockId,
                    "APPEND_ROWS",
                    [TableMetricFixture(DiffMetricKey, "NUMBER")],
                    rowLabelDataType: "NUMBER")
            ]);
        var template = _diffTableFixture.Form.Template;
        var fixedAt = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
        template.Code = "P8_DIFF_CONFIG_FORM";
        template.Name = "P8 Diff Configuration typed concepts";
        template.Description = "P8-06 isolated configuration-only fixture";
        template.IsPublished = true;
        template.PublishedAtUtc = fixedAt;
        template.PublishedByUserId = _adminId;
        var published = DynamicFormPublishedSchemaSnapshotBuilder.Build(template);
        template.PublishedSchemaSnapshotJson = published.Json;
        template.PublishedSchemaHash = published.Sha256;

        await _database.GetCollection<DynamicFormTemplate>(DynamicFormsCollection)
            .InsertOneAsync(template, cancellationToken: ct);

        var field = _diffTableFixture.Form.Fields.Single();
        await PatchFieldConfigAsync(
            actor,
            _diffTableFixture.Form,
            "p8-dif-setup-field-statistics",
            FieldPayload(FieldPatch(
                field.Id,
                ["COUNT", "SUM"],
                statisticLabelCodes: [_p806FieldLabel.LabelCode])),
            ct);
        (_, _diffStatisticConfig) = await PatchTableConfigAsync(
            actor,
            _diffTableFixture,
            "p8-dif-setup-table-statistics",
            TablePayload(TablePatch(
                DiffBlockId,
                "APPEND_ROWS",
                false,
                [TableMetric(DiffMetricKey, "NUMBER", ["COUNT", "SUM"])],
                metricLabelTargets:
                [
                    TableMetricLabelTarget(
                        DiffMetricKey,
                        _p806MetricLabel.LabelCode)
                ],
                allowedRowLabelCodes: [_p806RowLabel.LabelCode])),
            ct);

        var fixtures = Enumerable.Range(1, 16)
            .Select((value, index) => BuildDiffAssignmentFixture(
                $"{value:000}",
                index,
                template))
            .ToArray();
        await _database.GetCollection<WorkAssignment>("work_assignments")
            .InsertManyAsync(
                fixtures.Select(fixture => fixture.Assignment),
                cancellationToken: ct);
        foreach (var fixture in fixtures)
            _diffFixtures.Add(fixture.Key, fixture);

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-06-fixtures.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                form = new
                {
                    template.Id,
                    template.Code,
                    template.VersionNo,
                    template.Revision,
                    template.IsPublished,
                    template.PublishedSchemaHash,
                    field = new
                    {
                        field.Id,
                        field.Key,
                        dataType = "NUMBER",
                        conceptCode = _p806FieldLabel.LabelCode
                    },
                    tableMetric = new
                    {
                        blockId = DiffBlockId,
                        metricKey = DiffMetricKey,
                        conceptKey = $"{DiffBlockId}:{DiffMetricKey}",
                        dataType = "NUMBER",
                        conceptCode = _p806MetricLabel.LabelCode
                    },
                    rowLabel = new
                    {
                        blockId = DiffBlockId,
                        labelCode = _p806RowLabel.LabelCode,
                        conceptKey = $"{DiffBlockId}:{_p806RowLabel.LabelCode}",
                        dataType = "NUMBER"
                    },
                    statisticConfig = new
                    {
                        _diffStatisticConfig.ConfigId,
                        _diffStatisticConfig.VersionId,
                        _diffStatisticConfig.VersionNo,
                        _diffStatisticConfig.Revision,
                        _diffStatisticConfig.ConfigHash,
                        _diffStatisticConfig.FieldSectionHash,
                        _diffStatisticConfig.TableSectionHash
                    }
                },
                assignments = fixtures.Select(fixture => new
                {
                    fixture.Key,
                    fixture.Assignment.Id,
                    fixture.Assignment.WorkId,
                    fixture.Assignment.DynamicFormTemplateId,
                    fixture.FlowInstanceId,
                    fixture.FlowStepId,
                    fixture.FlowBranchId
                }),
                fixtureWritesOutsideCaseDeltas = true
            },
            ct);
    }

    private P8DiffFixture BuildDiffAssignmentFixture(
        string key,
        int index,
        DynamicFormTemplate template)
    {
        var actor = Actor("system_admin");
        var assignmentId = ObjectId.GenerateNewId().ToString();
        var flowInstanceId = ObjectId.GenerateNewId().ToString();
        var flowBranchId = ObjectId.GenerateNewId().ToString();
        var flowStepId = $"p8-diff-step-{key}";
        var fixedAt = new DateTime(2026, 8, 2, 0, 3, 0, DateTimeKind.Utc)
            .AddSeconds(index);
        var assignment = new WorkAssignment
        {
            Id = assignmentId,
            WorkId = ObjectId.GenerateNewId().ToString(),
            DynamicFormTemplateId = template.Id,
            DynamicFormTemplateCode = template.Code,
            DynamicFormTemplateName = template.Name,
            DynamicFormFamilyId = template.FamilyId,
            DynamicFormVersionNo = template.VersionNo,
            WorkType = "P8_DIFF_CONFIG",
            AssignmentType = "ONCE",
            AggregationType = "NONE",
            Assignees =
            [
                new UserRef
                {
                    UserId = actor.Id,
                    Username = actor.Username,
                    FullName = "P8 Diff owner",
                    UnitId = actor.UnitId,
                    UnitSymbol = "ROOT",
                    UnitShortName = "ROOT",
                    UnitName = "P8 Root"
                }
            ],
            LeaderWatcherUserIds = key == "011"
                ? [actor.Id, Actor("ordinary_a").Id]
                : [actor.Id],
            IsActive = true,
            RootAssignmentId = assignmentId,
            Level = 0,
            Code = $"P8-DIFF-{key}",
            Name = $"P8 Diff owner {key}",
            Path = $"/{assignmentId}/",
            FlowInstanceId = flowInstanceId,
            FlowStepId = flowStepId,
            FlowStepCode = $"DIFF_STEP_{key}",
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
        return new P8DiffFixture(
            key,
            template.Id,
            assignment,
            flowInstanceId,
            flowStepId,
            flowBranchId);
    }

    private P8DiffFixture DiffFixture(string key)
        => _diffFixtures.TryGetValue(key, out var fixture)
            ? fixture
            : throw new HarnessCaseNotRunnableException(
                $"P8 Diff fixture {key} was not seeded.");
}

internal sealed record P8DiffFixture(
    string Key,
    string DynamicFormTemplateId,
    WorkAssignment Assignment,
    string FlowInstanceId,
    string FlowStepId,
    string FlowBranchId);
