using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsRun;

var cases = new (string Name, Func<Task> Run)[]
{
    ("api families fail closed before owner reads", ApiFamiliesFailClosedAsync),
    ("advanced requires final capture", AdvancedRequiresCaptureAsync),
    ("advanced raw and rendered views complete", AdvancedCompleteAsync),
    ("advanced raw one-bit drift fails", AdvancedRawDriftAsync),
    ("advanced final export drift fails", AdvancedExportDriftAsync),
    ("incomplete resolution is deterministic", IncompleteDeterministicAsync),
    ("direct nullable columns use shared schema",
        DirectNullableColumnsUseSharedSchemaAsync),
    ("direct aggregate boundary equality is semantic",
        DirectAggregateBoundaryEqualityAsync),
    ("direct aggregate boundary drift fails closed",
        DirectAggregateBoundaryDriftAsync),
    ("direct generation scopes select target assignment",
        DirectGenerationScopesSelectTargetAsync),
    ("direct generation axis drift fails closed",
        DirectGenerationAxisDriftAsync),
    ("owner DI registration is scoped", OwnerDiRegistration)
};

var passed = 0;
foreach (var item in cases)
{
    await item.Run();
    passed++;
}
Console.WriteLine($"P10_T23_CROSS_VIEW_OWNER_OK checks={passed}");

static async Task ApiFamiliesFailClosedAsync()
{
    foreach (var kind in new[]
             {
                 StatRunExportResultKinds.DirectField,
                 StatRunExportResultKinds.Basic,
                 StatRunExportResultKinds.Flow,
                 StatRunExportResultKinds.Diff
             })
    {
        var advanced = new FakeAdvancedOwner();
        var export = new FakeExportOwner();
        var owner = new StatisticReconciliationActualCrossViewV2Owner(
            advanced,
            export);
        var result = await owner.ResolveAsync(new(
            StatisticReconciliationActualCrossViewV2OwnerSchemas.Command,
            Fixture.Material(kind, null, null),
            null, null, null, null, null, null, null, null));
        Equal(StatisticReconciliationActualCrossViewV2OwnerStates.Incomplete,
            result.State);
        Equal(StatisticReconciliationActualCrossViewV2OwnerFailures
            .AuthorizationRelationUnavailable, result.FailureCode);
        Equal(4, result.RequiredPersistenceFields.Length);
        Equal(0, advanced.ReadCount);
        Equal(0, export.ReadCount);
    }
}

static async Task AdvancedRequiresCaptureAsync()
{
    var fixture = Fixture.Advanced();
    var advanced = new FakeAdvancedOwner(fixture.Node);
    var export = new FakeExportOwner(fixture.Artifact);
    var owner = new StatisticReconciliationActualCrossViewV2Owner(
        advanced,
        export);
    var result = await owner.ResolveAsync(new(
        StatisticReconciliationActualCrossViewV2OwnerSchemas.Command,
        fixture.Material,
        null, null, null, null, null, null, null, null));
    Equal(StatisticReconciliationActualCrossViewV2OwnerFailures
        .AdvancedCaptureRequired, result.FailureCode);
    Equal(0, advanced.ReadCount);
    Equal(0, export.ReadCount);
}

static async Task AdvancedCompleteAsync()
{
    var fixture = Fixture.Advanced();
    var result = await fixture.Owner.ResolveAsync(fixture.Command);
    Equal(StatisticReconciliationActualCrossViewV2OwnerStates.Complete,
        result.State);
    Equal(StatisticReconciliationActualCrossViewV2OwnerFailures.None,
        result.FailureCode);
    NotNull(result.Plan);
    NotNull(result.Base);
    NotNull(result.Actual);
    True(StatisticReconciliationActualCrossViewParityV2.Prove(
        result.Plan, result.Base, result.Actual).Complete);
    Equal(1, fixture.AdvancedOwner.ReadCount / 3);
    Equal(1, fixture.ExportOwner.ReadCount);
}

static async Task AdvancedRawDriftAsync()
{
    var fixture = Fixture.Advanced();
    fixture.Node.ValueJson = fixture.Node.ValueJson.Replace(
        "alpha", "alphb", StringComparison.Ordinal);
    fixture.Node.ValueHash = RawSha(fixture.Node.ValueJson);
    var result = await fixture.Owner.ResolveAsync(fixture.Command);
    Equal(StatisticReconciliationActualCrossViewV2OwnerFailures
        .AdvancedOwnerDrift, result.FailureCode);
}

static async Task AdvancedExportDriftAsync()
{
    var fixture = Fixture.Advanced();
    var changed = fixture.Command.Export! with
    {
        CaptureSemanticSha256 = Hash('f')
    };
    var result = await fixture.Owner.ResolveAsync(fixture.Command with
    {
        Export = changed
    });
    Equal(StatisticReconciliationActualCrossViewV2OwnerFailures
        .ExportOwnerDrift, result.FailureCode);
}

static async Task IncompleteDeterministicAsync()
{
    var owner = new StatisticReconciliationActualCrossViewV2Owner(
        new FakeAdvancedOwner(), new FakeExportOwner());
    var command = new StatisticReconciliationActualCrossViewV2OwnerCommand(
        StatisticReconciliationActualCrossViewV2OwnerSchemas.Command,
        Fixture.Material(StatRunExportResultKinds.Basic, null, null),
        null, null, null, null, null, null, null, null);
    var first = await owner.ResolveAsync(command);
    var second = await owner.ResolveAsync(command);
    Equal(first.SchemaVersion, second.SchemaVersion);
    Equal(first.State, second.State);
    Equal(first.FailureCode, second.FailureCode);
    True(first.RequiredPersistenceFields.SequenceEqual(
        second.RequiredPersistenceFields, StringComparer.Ordinal));
    Equal(first.Plan, second.Plan);
    Equal(first.Base, second.Base);
    Equal(first.Actual, second.Actual);
    Equal(first.ResolutionSha256, second.ResolutionSha256);
}

static Task DirectNullableColumnsUseSharedSchemaAsync()
{
    var row = new FieldStatisticSummaryRow
    {
        WorkId = "work-a",
        ScopeType = "ASSIGNMENT",
        ScopeId = "assignment-a",
        DynamicFormTemplateId = "template-a",
        FieldId = "amount",
        FieldKey = "amount",
        FieldLabel = "Amount",
        FieldType = "NUMBER",
        StatisticLabelCodes = ["amount"],
        ShowInTree = true,
        PeriodKey = "2026-08",
        PeriodInstanceKey = "period-a",
        PeriodKind = "MONTH",
        ReportStatus = 2,
        ValueCount = 1,
        NumericValueCount = 1,
        Sum = 10m,
        Min = 10m,
        Max = 10m,
        Average = 10m,
        ReportCount = 1,
        UpdatedAtUtc = new DateTime(
            2026, 8, 14, 1, 2, 3, DateTimeKind.Utc)
    };
    var json = StatisticReconciliationActualJson.Canonicalize(
        JsonSerializer.SerializeToElement(
            row,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    var manifest = new StatisticReconciliationActualExportManifest(
        StatisticReconciliationActualExportParser.RequiredSchemaVersion,
        "export-direct",
        Hash('1'),
        Hash('2'),
        StatisticReconciliationActualExportFormats.Csv,
        StatRunExportResultKinds.DirectField,
        "text/csv; charset=utf-8",
        "direct.csv",
        Hash('3'),
        1,
        1,
        33,
        "work-a",
        "ASSIGNMENT",
        "assignment-a",
        "p9-run",
        Hash('4'),
        Hash('5'),
        Hash('6'),
        Hash('7'),
        1,
        "catalog-v1",
        Hash('8'),
        Hash('9'),
        Hash('a'),
        "chain-a",
        "prompt-a",
        1,
        Hash('b'),
        new DateTime(2026, 8, 14, 2, 0, 0, DateTimeKind.Utc),
        [],
        "period-a",
        "{}");
    var projection =
        StatisticReconciliationActualCrossViewV2ExportBaseProjector
            .ProjectJson(json, manifest);
    var bucket = projection.Base.Columns.Single(value =>
        value.Name == "bucketKey");
    var earliest = projection.Base.Columns.Single(value =>
        value.Name == "earliestDateUtc");
    Equal(StatisticReconciliationActualExportValueTypes.Text,
        bucket.ValueType);
    Equal(StatisticReconciliationActualExportBlankPolicies.Null,
        bucket.BlankPolicy);
    Equal(StatisticReconciliationActualExportValueTypes.UtcInstant,
        earliest.ValueType);
    Equal(StatisticReconciliationActualExportBlankPolicies.Null,
        earliest.BlankPolicy);

    var tampered = json.Replace(
        "\"earliestDateUtc\":null",
        "\"earliestDateUtc\":\"not-an-instant\"",
        StringComparison.Ordinal);
    True(tampered != json);
    Throws(() =>
        StatisticReconciliationActualCrossViewV2ExportBaseProjector
            .ProjectJson(tampered, manifest));
    return Task.CompletedTask;
}

static Task DirectAggregateBoundaryEqualityAsync()
{
    var material = DirectAggregateBoundary();
    var captured = material with
    {
        Direct = material.Direct with
        {
            WorkId = string.Concat(material.Direct.WorkId)
        },
        AggregateStoreDigests = material.AggregateStoreDigests
            .Select(value => value with
            {
                Store = string.Concat(value.Store)
            })
            .ToImmutableArray()
    };
    True(StatisticReconciliationActualCrossViewV3DirectParityProjector
        .SameBoundary(captured, material));
    return Task.CompletedTask;
}

static Task DirectAggregateBoundaryDriftAsync()
{
    var material = DirectAggregateBoundary();
    var first = material.AggregateStoreDigests[0];
    var drifted = material with
    {
        AggregateStoreDigests = material.AggregateStoreDigests.SetItem(
            0, first with { RowCount = first.RowCount + 1 })
    };
    True(!StatisticReconciliationActualCrossViewV3DirectParityProjector
        .SameBoundary(drifted, material));
    return Task.CompletedTask;
}

static Task DirectGenerationScopesSelectTargetAsync()
{
    var command = DirectScopeCommand();
    var rows = new[]
    {
        (Name: "work", WorkId: "work-a", ScopeType: "WORK",
            ScopeId: "work-a", TemplateId: (string?)"template-a",
            Period: "period-a"),
        (Name: "root", WorkId: "work-a", ScopeType: "ROOT",
            ScopeId: "root-a", TemplateId: (string?)"template-a",
            Period: "period-a"),
        (Name: "ancestor", WorkId: "work-a", ScopeType: "ASSIGNMENT",
            ScopeId: "assignment-parent", TemplateId: (string?)"template-a",
            Period: "period-a"),
        (Name: "target", WorkId: "work-a", ScopeType: "ASSIGNMENT",
            ScopeId: "assignment-a", TemplateId: (string?)"template-a",
            Period: "period-a")
    };
    var selected = StatisticReconciliationActualCrossViewV3DirectParityProjector
        .SelectTargetRows(command, rows, value =>
            (value.WorkId, value.ScopeType, value.ScopeId,
                value.TemplateId, value.Period));
    Equal(1, selected.Length);
    Equal("target", selected[0].Name);
    return Task.CompletedTask;
}

static Task DirectGenerationAxisDriftAsync()
{
    var command = DirectScopeCommand();
    Throws(() => StatisticReconciliationActualCrossViewV3DirectParityProjector
        .SelectTargetRows(command,
            new[]
            {
                (WorkId: "work-b", ScopeType: "ASSIGNMENT",
                    ScopeId: "assignment-a", TemplateId: (string?)"template-a",
                    Period: "period-a")
            },
            value => (value.WorkId, value.ScopeType, value.ScopeId,
                value.TemplateId, value.Period)));
    Throws(() => StatisticReconciliationActualCrossViewV3DirectParityProjector
        .SelectTargetRows(command,
            new[]
            {
                (WorkId: "work-a", ScopeType: "UNIT", ScopeId: "",
                    TemplateId: (string?)"template-a", Period: "period-a")
            },
            value => (value.WorkId, value.ScopeType, value.ScopeId,
                value.TemplateId, value.Period)));
    return Task.CompletedTask;
}

static StatisticReconciliationActualCrossViewV2OwnerCommand DirectScopeCommand()
{
    var material = Fixture.Material(
        StatRunExportResultKinds.DirectField, null, null) with
    {
        Run = new StatisticReconciliationRun
        {
            WorkId = "work-a",
            ScopeAssignmentId = "assignment-a",
            DynamicFormVersionId = "template-a",
            PeriodInstanceKey = "period-a"
        }
    };
    return new StatisticReconciliationActualCrossViewV2OwnerCommand(
        StatisticReconciliationActualCrossViewV2OwnerSchemas.Command,
        material, null, null, null, null, null, null, null, null);
}
static ActualAggregatePublicationBoundary DirectAggregateBoundary()
{
    var direct = new ActualDirectProjectionBoundary(
        "work-a", "period-a", "form-family-a", "form-version-a", 1,
        Hash('1'), "run-a", "generation-a", Hash('2'), "event-a",
        DateTime.UnixEpoch, 1, "config-a", "config-version-a", 1, 1,
        Hash('3'), "chain-a", "1.7", Hash('4'), Hash('5'), Hash('6'),
        Hash('7'), Hash('8'), Hash('9'));
    return new ActualAggregatePublicationBoundary(
        direct, "scope-a", 1, "FRESH", DateTime.UnixEpoch,
        [new ActualAggregateStoreDigestPin("store-a", 1, Hash('a'))]);
}
static Task OwnerDiRegistration(){
    var services = new ServiceCollection();
    services.AddStatisticReconciliationActualCrossViewV2Owner();
    var matches = services.Where(value => value.ServiceType ==
            typeof(IStatisticReconciliationActualCrossViewV2Owner))
        .ToArray();
    Equal(1, matches.Length);
    Equal(ServiceLifetime.Scoped, matches[0].Lifetime);
    Equal(typeof(StatisticReconciliationActualCrossViewV2Owner),
        matches[0].ImplementationType);
    return Task.CompletedTask;
}

static string Hash(char value) => new(value, 64);
static string RawSha(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)))
    .ToLowerInvariant();
static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}
static void Throws(Action action)
{
    try
    {
        action();
    }
    catch (StatisticReconciliationActualObservationException)
    {
        return;
    }
    throw new InvalidOperationException("Expected observation failure.");
}
static void NotNull(object? value)
{
    if (value is null) throw new InvalidOperationException("Expected value.");
}
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(
            $"Expected {expected}, actual {actual}.");
}

internal sealed class FakeAdvancedOwner(
    WorkAssignmentAdvancedSummaryDayNode? node = null)
    : IStatisticReconciliationActualAdvancedOwnerReader
{
    internal int ReadCount { get; private set; }

    public Task<IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode>>
        ReadDayNodesAsync(
            ActualAdvancedOwnerBoundary boundary,
            CancellationToken cancellationToken)
    {
        ReadCount++;
        return Task.FromResult<IReadOnlyList<
            WorkAssignmentAdvancedSummaryDayNode>>(
            node is null ? [] : [node]);
    }

    public Task<IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode>>
        ReadMonthNodesAsync(
            ActualAdvancedOwnerBoundary boundary,
            CancellationToken cancellationToken)
    {
        ReadCount++;
        return Task.FromResult<IReadOnlyList<
            WorkAssignmentAdvancedSummaryMonthNode>>([]);
    }

    public Task<IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode>>
        ReadYearNodesAsync(
            ActualAdvancedOwnerBoundary boundary,
            CancellationToken cancellationToken)
    {
        ReadCount++;
        return Task.FromResult<IReadOnlyList<
            WorkAssignmentAdvancedSummaryYearNode>>([]);
    }
}

internal sealed class FakeExportOwner(
    StatisticReconciliationActualExportArtifact? artifact = null)
    : IStatisticReconciliationActualExportOwnerReader
{
    internal int ReadCount { get; private set; }

    public Task<StatisticReconciliationActualExportOwnerRead>
        ReadArtifactAsync(
            StatisticReconciliationActualExportOwnerTarget target,
            CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromResult(artifact is null
            ? StatisticReconciliationActualExportOwnerRead.Stale(
                "TEST_ARTIFACT_MISSING")
            : StatisticReconciliationActualExportOwnerRead.Ready(artifact));
    }
}

internal sealed record AdvancedFixture(
    StatisticReconciliationActualTrustedCaptureMaterial Material,
    WorkAssignmentAdvancedSummaryDayNode Node,
    StatisticReconciliationActualExportArtifact Artifact,
    FakeAdvancedOwner AdvancedOwner,
    FakeExportOwner ExportOwner,
    StatisticReconciliationActualCrossViewV2Owner Owner,
    StatisticReconciliationActualCrossViewV2OwnerCommand Command);

internal static class Fixture
{
    private const string Work = "work-a";
    private const string Assignment = "assignment-a";
    private const string Template = "template-a";
    private const string Result = "advanced-node-a";
    private const string Period = "period-a";
    private const string Json = "{\"name\":\"alpha\",\"totalCount\":2}";
    private const string Filter =
        "{\"blockId\":null,\"bucketKey\":null," +
        "\"dynamicFormTemplateId\":null,\"fieldId\":null," +
        "\"fieldKey\":null,\"labelCode\":null," +
        "\"metricKey\":null,\"periodKey\":null}";

    internal static AdvancedFixture Advanced()
    {
        var configSha = Hash('c');
        var sourceSha = Hash('6');
        var valueSha = RawSha(Json);
        var boundary = Boundary(configSha);
        var node = Node(boundary, valueSha, sourceSha);
        var (artifact, columnSha) = Artifact(
            configSha, sourceSha, valueSha);
        var target = Target(StatRunExportResultKinds.Advanced,
            artifact.Manifest, columnSha);
        var material = Material(
            StatRunExportResultKinds.Advanced, boundary, target);
        var finalAdvanced = AdvancedCapture(boundary, node);
        var parser = new StatisticReconciliationActualExportParser();
        var finalExport = parser.Parse(artifact);
        var advancedOwner = new FakeAdvancedOwner(node);
        var exportOwner = new FakeExportOwner(artifact);
        var owner = new StatisticReconciliationActualCrossViewV2Owner(
            advancedOwner, exportOwner);
        var command = new StatisticReconciliationActualCrossViewV2OwnerCommand(
            StatisticReconciliationActualCrossViewV2OwnerSchemas.Command,
            material,
            null, null, null, null, finalAdvanced, null, null, finalExport);
        return new(material, node, artifact, advancedOwner, exportOwner, owner,
            command);
    }

    internal static StatisticReconciliationActualTrustedCaptureMaterial Material(
        string kind,
        ActualAdvancedOwnerBoundary? advanced,
        StatisticReconciliationActualExportOwnerTarget? export)
    {
        export ??= new StatisticReconciliationActualExportOwnerTarget(
            "export-a", kind, Work, "ASSIGNMENT", Assignment, Result,
            Hash('1'), Hash('2'), Hash('3'), Hash('4'), Hash('5'), Hash('6'),
            Hash('7'), Period, Hash('8'));
        return new(
            new StatisticReconciliationRun(),
            "P10_TEST_BOUNDARY_V1",
            null!, null!, null!, null!, advanced!, null!,
            new StatisticReconciliationActualApiCaptureRequest(
                null, null, null, null, null, null, 0, []),
            export,
            null!);
    }

    private static ActualAdvancedOwnerBoundary Boundary(string configSha)
        => new(
            Work, Assignment, Template, "section-a", "config-a",
            "config-version-a", 1, 1, configSha, [], "UTC_GREGORIAN",
            "chain-a", "P9-05", 3, Hash('a'), Hash('b'), Hash('d'),
            [Result], [], []);

    private static WorkAssignmentAdvancedSummaryDayNode Node(
        ActualAdvancedOwnerBoundary boundary,
        string valueSha,
        string sourceSha)
        => new()
        {
            Id = Result,
            WorkId = boundary.WorkId,
            AssignmentId = boundary.AssignmentId,
            DynamicFormTemplateId = boundary.DynamicFormTemplateId,
            SectionId = boundary.SectionId,
            ConfigId = boundary.ConfigId,
            ConfigVersionId = boundary.ConfigVersionId,
            ConfigVersionNo = boundary.ConfigVersionNo,
            ConfigRevision = boundary.ConfigRevision,
            ConfigHash = boundary.ConfigSha256,
            DependencyPins = [],
            TimeAxis = boundary.TimeAxis,
            CandidateChainId = boundary.CandidateChainId,
            CandidatePromptId = boundary.CandidatePromptId,
            CandidateStage = boundary.CandidateStage,
            CandidateCatalogRawSha256 = boundary.CandidateCatalogRawSha256,
            CandidateCatalogSemanticSha256 =
                boundary.CandidateCatalogSemanticSha256,
            CandidateStageLockSha256 = boundary.CandidateStageLockSha256,
            Grain = WorkAssignmentAdvancedSummaryHierarchyGrains.Day,
            GrainKey = "2026-08-12",
            DayKey = "2026-08-12",
            WindowStartUtc = new DateTime(2026, 8, 12, 0, 0, 0,
                DateTimeKind.Utc),
            WindowEndExclusiveUtc = new DateTime(2026, 8, 13, 0, 0, 0,
                DateTimeKind.Utc),
            Status = WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean,
            IsDirty = false,
            SourceSignatureHash = sourceSha,
            SourceReportCount = 0,
            SourceReportIds = [],
            InputNodeKeys = [],
            ValueJson = Json,
            ValueHash = valueSha,
            BuiltAtUtc = new DateTime(2026, 8, 12, 1, 0, 0,
                DateTimeKind.Utc)
        };

    private static ActualAdvancedCapture AdvancedCapture(
        ActualAdvancedOwnerBoundary boundary,
        WorkAssignmentAdvancedSummaryDayNode node)
    {
        var raw = RawSha(node.ValueJson);
        var state = new ActualAdvancedNodeState(
            WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean,
            false, null, false, true, true, true, true, true, true, true,
            true, true, true, Hash('e'));
        var observation = new ActualAdvancedNodeObservation(
            StatisticReconciliationActualAdvancedStores.Day,
            node.Id,
            node.Grain,
            node.GrainKey,
            node.DayKey,
            null,
            null,
            node.WindowStartUtc,
            node.WindowEndExclusiveUtc,
            node.SourceSignatureHash,
            node.SourceReportCount,
            [],
            [],
            node.ValueJson,
            node.ValueHash,
            raw,
            RawSha(node.ValueJson),
            node.BuiltAtUtc,
            null,
            state,
            Hash('9'));
        return new(boundary, [observation], 1, Hash('0'));
    }

    private static (
        StatisticReconciliationActualExportArtifact Artifact,
        string ColumnSha) Artifact(
            string configSha,
            string sourceSha,
            string resultSha)
    {
        var columns = ImmutableArray.Create(
            new StatisticReconciliationActualExportColumnContract(
                0, "ordinal", "INTEGER", "FORBIDDEN"),
            new StatisticReconciliationActualExportColumnContract(
                1, "name", "TEXT", "FORBIDDEN"),
            new StatisticReconciliationActualExportColumnContract(
                2, "totalCount", "DECIMAL", "FORBIDDEN", true));
        var sidecar = StatRunExportColumnManifestContract.Create(
            columns.Select(value => new StatRunExportColumnManifestEntry(
                value.Ordinal, value.Name, value.ValueType, value.BlankPolicy,
                value.IsFullFilterTotal)).ToArray());
        var content = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(
                "\"ordinal\",\"name\",\"totalCount\"\r\n" +
                "\"1\",\"alpha\",\"2\"\r\n"))
            .ToArray();
        var contentSha = Convert.ToHexString(SHA256.HashData(content))
            .ToLowerInvariant();
        var filterSha = RawSha(Filter);
        var ownerSha = StatRunExportColumnManifestContract
            .ComputeOwnerSemanticSha256(
                StatRunExportResultKinds.Advanced,
                Work,
                "ASSIGNMENT",
                Assignment,
                Result,
                resultSha,
                configSha,
                sourceSha,
                filterSha,
                0,
                1,
                columns.Length,
                sidecar.Sha256);
        var manifest = new StatisticReconciliationActualExportManifest(
            StatisticReconciliationActualExportParser.RequiredSchemaVersion,
            "export-a",
            Hash('1'),
            Hash('2'),
            StatisticReconciliationActualExportFormats.Csv,
            StatRunExportResultKinds.Advanced,
            "text/csv; charset=utf-8",
            "advanced.csv",
            contentSha,
            content.Length,
            1,
            columns.Length,
            Work,
            "ASSIGNMENT",
            Assignment,
            Result,
            resultSha,
            configSha,
            sourceSha,
            filterSha,
            0,
            "P9_CATALOG_V1",
            Hash('3'),
            Hash('4'),
            Hash('5'),
            "chain-a",
            "P9-EXPORT",
            7,
            ownerSha,
            new DateTime(2026, 8, 12, 2, 0, 0, DateTimeKind.Utc),
            columns,
            Period,
            Filter);
        var parser = new StatisticReconciliationActualExportParser();
        return (new StatisticReconciliationActualExportArtifact(
            manifest,
            parser.ComputeManifestSha256(manifest),
            content), sidecar.Sha256);
    }

    private static StatisticReconciliationActualExportOwnerTarget Target(
        string kind,
        StatisticReconciliationActualExportManifest manifest,
        string columnSha)
        => new(
            manifest.ExportId,
            kind,
            manifest.WorkId,
            manifest.ScopeType,
            manifest.ScopeId,
            manifest.ResultId,
            manifest.SourceSha256,
            manifest.ConfigSha256,
            manifest.RequestSha256,
            manifest.AuthorizationSnapshotSha256,
            manifest.ContentSha256,
            columnSha,
            manifest.OwnerSemanticSha256,
            manifest.PeriodInstanceKey,
            manifest.FilterSha256);

    private static string Hash(char value) => new(value, 64);
    private static string RawSha(string value) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();
}
