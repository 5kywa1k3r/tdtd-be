using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class FullCaptureServiceIntegration
{
    internal static async Task RunAsync()
    {
        var source = ActualFixture.Source(ActualFixture.Report1);
        var sourceReader = new SourceReader([source]);
        var sourceCapture = await new StatisticReconciliationActualSourceMembershipAdapter()
            .CaptureAsync(ActualFixture.Scope(), sourceReader);
        var directBoundary = ActualFixture.Boundary();
        var directField = ActualFixture.Field(
            directBoundary,
            source,
            "field-amount",
            "amount",
            "NUMBER",
            numeric: 7m);
        var directReader = new ProjectionReader([directField], [], []);

        var aggregateField = AggregateField(directBoundary, source);
        var aggregateBoundary = AggregateBoundary(directBoundary, [aggregateField]);
        var aggregateReader = new FullAggregateReader([aggregateField], [], []);

        var basicBoundary = InvokeFixture<ActualBasicOwnerBoundary>(
            "BasicBoundary",
            StatisticReconciliationActualBasicModes.FlowFinal) with
        {
            WorkId = ActualFixture.WorkId,
            ScopeAssignmentId = ActualFixture.Assignment1,
            DynamicFormTemplateId = ActualFixture.TemplateId
        };
        var basicOwner = InvokeFixture<WorkAssignmentBasicSummarySnapshot>(
            "BasicOwner",
            basicBoundary,
            false,
            true);
        Require(!basicOwner.SourceReportIds.Contains(
                source.ReportId,
                StringComparer.Ordinal),
            "DIRECT_AND_BASIC_REPORT_SETS_MUST_BE_DISTINCT");

        var advancedBoundary = InvokeFixture<ActualAdvancedOwnerBoundary>(
            "AdvancedBoundary") with
        {
            WorkId = ActualFixture.WorkId,
            AssignmentId = ActualFixture.Assignment1,
            DynamicFormTemplateId = ActualFixture.TemplateId
        };
        var advancedNodes = InvokeFixture<(
            WorkAssignmentAdvancedSummaryDayNode Day,
            WorkAssignmentAdvancedSummaryMonthNode Month,
            WorkAssignmentAdvancedSummaryYearNode Year)>(
            "AdvancedNodes",
            advancedBoundary);

        var diffBoundary = InvokeFixture<ActualP9DiffOwnerBoundary>(
            "DiffBoundary",
            "FIELD") with
        {
            WorkId = ActualFixture.WorkId,
            AssignmentId = ActualFixture.Assignment1,
            DynamicFormTemplateId = ActualFixture.TemplateId
        };
        var diffRows = InvokeFixture<List<P9StatisticDiffResultRow>>(
            "FieldRows",
            "FIELD");
        var diffOwner = InvokeFixture<WorkReportStatisticDiffResult>(
            "DiffOwner",
            diffBoundary,
            "FIELD",
            diffRows);

        var filterSha256 = StatisticReconciliationActualJson.RawSha256("{}");
        var configurationSha256 = Hash("full-service-configuration-bundle");
        var apiOwner = new FullApiOwnerReader();
        var authorization = await apiOwner.AuthorizeAsync(
            new StatisticReconciliationActualApiAuthorizationProbe(
                ActualFixture.WorkId,
                ActualFixture.Assignment1),
            default);
        var boundary = CoherentBoundary(
            sourceCapture.SourceSetSha256,
            configurationSha256,
            filterSha256,
            Hash("run-authorization-owner"));
        var export = ExportArtifact(boundary);
        Require(!StringComparer.Ordinal.Equals(
                    boundary.SourceSetSha256,
                    export.Manifest.SourceSha256) &&
                !StringComparer.Ordinal.Equals(
                    boundary.ConfigurationBundleSha256,
                    export.Manifest.ConfigSha256),
            "P9_EXPORT_AND_P10_BOUNDARY_HASH_DOMAINS_DISTINCT");
        Require(!StringComparer.Ordinal.Equals(
                    boundary.AuthorizationSnapshotSha256,
                    authorization.AuthorizationSnapshotSha256) &&
                !StringComparer.Ordinal.Equals(
                    boundary.AuthorizationSnapshotSha256,
                    export.Manifest.AuthorizationSnapshotSha256) &&
                !StringComparer.Ordinal.Equals(
                    authorization.AuthorizationSnapshotSha256,
                    export.Manifest.AuthorizationSnapshotSha256),
            "RUN_API_EXPORT_AUTHORIZATION_DOMAINS_DISTINCT");
        var exportTarget = new StatisticReconciliationActualExportOwnerTarget(
            export.Manifest.ExportId,
            export.Manifest.ResultKind,
            export.Manifest.WorkId,
            export.Manifest.ScopeType,
            export.Manifest.ScopeId,
            export.Manifest.ResultId,
            export.Manifest.SourceSha256,
            export.Manifest.ConfigSha256,
            export.Manifest.RequestSha256,
            export.Manifest.AuthorizationSnapshotSha256,
            export.Manifest.ContentSha256,
            Hash("full-service-column-sidecar"),
            export.Manifest.OwnerSemanticSha256,
            ActualFixture.PeriodInstanceKey,
            export.Manifest.FilterSha256);

        var events = new List<string>();
        var backend = new MemoryBackend(events);
        var cas = new CountingCas(events);
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build();
        services.AddLogging();
        services.AddStatisticReconciliationActualCapture(configuration);
        services.AddSingleton<IStatisticReconciliationActualCoherentBoundaryReaderFactory>(
            new FullBoundaryFactory(boundary));
        services.AddSingleton<IStatisticReconciliationActualSourceOwnerReader>(
            sourceReader);
        services.AddSingleton<IStatisticReconciliationActualDirectProjectionOwnerReader>(
            directReader);
        services.AddSingleton<IStatisticReconciliationActualAggregateOwnerReader>(
            aggregateReader);
        services.AddSingleton<IStatisticReconciliationActualBasicOwnerReader>(
            new FullBasicReader(basicOwner));
        services.AddSingleton<IStatisticReconciliationActualAdvancedOwnerReader>(
            new FullAdvancedReader(
                [advancedNodes.Day],
                [advancedNodes.Month],
                [advancedNodes.Year]));
        services.AddSingleton<IStatisticReconciliationActualP9DiffOwnerReader>(
            new FullDiffReader(diffOwner));
        services.AddSingleton<IStatisticReconciliationActualApiOwnerReader>(apiOwner);
        var exportOwner = new FullExportOwnerReader(exportTarget, export);
        services.AddSingleton<IStatisticReconciliationActualExportOwnerReader>(
            exportOwner);
        services.AddSingleton<IStatisticReconciliationActualObservationBackend>(backend);
        services.AddSingleton<IStatisticReconciliationActualGenerationCas>(cas);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var scoped = scope.ServiceProvider;
        var mapper = scoped.GetRequiredService<
            IStatisticReconciliationActualAdapterTypedMapper>();
        Require(mapper.GetType() ==
                typeof(StatisticReconciliationActualAdapterTypedMapper),
            "CONCRETE_MAPPER_REQUIRED");
        var service = scoped.GetRequiredService<
            StatisticReconciliationActualCaptureService>();
        var request = new StatisticReconciliationActualCaptureRequest(
            boundary.ReconciliationId,
            new StatisticReconciliationActualMongoBoundaryDescriptor(
                boundary.ReconciliationId,
                boundary.WorkId,
                boundary.ScopeAssignmentId,
                boundary.SourceSetSha256,
                boundary.ConfigurationBundleSha256,
                boundary.FilterSha256,
                boundary.AuthorizationSnapshotSha256,
                []),
            boundary,
            ActualFixture.Scope(),
            directBoundary,
            aggregateBoundary,
            new StatisticReconciliationActualBasicCaptureTarget(
                StatisticReconciliationActualBasicModes.FlowFinal,
                basicBoundary),
            advancedBoundary,
            diffBoundary,
            new StatisticReconciliationActualApiCaptureRequest(
                StatisticReconciliationActualApiSurfaces.DirectField,
                boundary.WorkId,
                boundary.ScopeAssignmentId,
                ActualFixture.TemplateId,
                "api-result-01",
                "{}",
                1,
                [new(0, 200)]),
            exportTarget,
            PublicationContext(boundary),
            Utc(13),
            "worker-full-service",
            "claim-full-service",
            Actor());

        var outcome = await service.CaptureAsync(request);
        Require(outcome.Published, "FULL_SERVICE_MUST_PUBLISH");
        var layers = outcome.Generation.OrderedLayerObservations;
        Require(layers.Length == 8, "FULL_SERVICE_EXACT_EIGHT_LAYERS");
        Require(layers.Select(layer => layer.Layer).SequenceEqual(
                StatisticReconciliationActualCoherentLayers.RequiredOrder,
                StringComparer.Ordinal),
            "FULL_SERVICE_LAYER_ORDER");
        Require(layers.All(layer =>
                layer.CaptureState ==
                    StatisticReconciliationActualCoherentCaptureStates.Ready &&
                layer.ObservationCount > 0),
            "FULL_SERVICE_EIGHT_READY_NONZERO_COUNTS");
        var typedLayers = layers.Count(layer => layer.TypedObservations.Length > 0);
        Require(typedLayers == 7, "FULL_SERVICE_TYPED_LAYER_COUNT_SEVEN");
        Require(layers[0].TypedObservations.Length == 0 &&
                layers.Skip(1).All(layer => layer.TypedObservations.Length > 0),
            "SOURCE_LAYER_MANIFEST_ONLY_SEVEN_RESULT_LAYERS_TYPED");
        Require(backend.ContentWrites == 1 && backend.CommitWrites == 1,
            "FULL_SERVICE_ONE_APPEND_SEQUENCE");
        Require(backend.ReadCalls >= 2, "FULL_SERVICE_COMMIT_READBACK_REQUIRED");
        Require(cas.PublishCalls == 1, "FULL_SERVICE_ONE_CAS");
        Require(events.IndexOf("APPEND_CONTENT") < events.IndexOf("APPEND_COMMIT") &&
                events.IndexOf("APPEND_COMMIT") < events.IndexOf("CAS"),
            "FULL_SERVICE_CONTENT_COMMIT_CAS_ORDER");

        var contentWrites = backend.ContentWrites;
        var commitWrites = backend.CommitWrites;
        var publishCalls = cas.PublishCalls;
        exportOwner.BeforeNextRead = () => apiOwner.Drift = true;
        var stale = await service.CaptureAsync(request);
        Require(!stale.Published &&
                stale.Generation.CaptureState ==
                StatisticReconciliationActualCoherentCaptureStates.Stale &&
                stale.Generation.StaleReason ==
                "FINAL_GUARD:API_OWNER_DRIFT",
            "API_FINAL_REPLAY_DRIFT_MUST_BE_STALE");
        Require(backend.ContentWrites == contentWrites &&
                backend.CommitWrites == commitWrites &&
                cas.PublishCalls == publishCalls,
            "API_FINAL_REPLAY_DRIFT_ZERO_APPEND_ZERO_CAS");
    }

    private static ActualAggregatePublicationBoundary AggregateBoundary(
        ActualDirectProjectionBoundary direct,
        IReadOnlyList<WorkReportFieldStatAggregate> fields)
        => new(
            direct,
            "DIRECT:full-service:work:period:template",
            7,
            "FRESH",
            new DateTime(2026, 8, 10, 12, 1, 0, DateTimeKind.Utc),
            ImmutableArray.Create(
                Digest(StatisticReconciliationActualAggregateStores.Field, fields),
                Digest(
                    StatisticReconciliationActualAggregateStores.RowLabel,
                    Array.Empty<WorkReportLabelStatAggregate>()),
                Digest(
                    StatisticReconciliationActualAggregateStores.TableMetric,
                    Array.Empty<WorkReportTableStatAggregate>())));

    private static WorkReportFieldStatAggregate AggregateField(
        ActualDirectProjectionBoundary boundary,
        ActualSourceOwnerRevision source)
    {
        var row = new WorkReportFieldStatAggregate
        {
            WorkId = ActualFixture.WorkId,
            ScopeType = "SYSTEM",
            ScopeId = ActualFixture.WorkId,
            RootAssignmentId = ActualFixture.Assignment1,
            DynamicFormTemplateId = ActualFixture.TemplateId,
            DynamicFormTemplateCode = "FORM-A",
            DynamicFormTemplateName = "Form A",
            FieldId = "field-amount",
            FieldKey = "amount",
            FieldLabel = "Amount",
            FieldType = "NUMBER",
            StatisticLabelCodes = ["finance"],
            ShowInTree = true,
            ShowInDetail = true,
            BucketKey = "all",
            BucketLabel = "All",
            PeriodKey = "2026-08",
            PeriodInstanceKey = ActualFixture.PeriodInstanceKey,
            PeriodKind = "MONTH",
            PeriodAnchorDate = Utc(1),
            PeriodStartDate = Utc(1),
            PeriodEndDate = Utc(31),
            CompletedDate = Utc(31),
            ReportStatus = 3,
            ReportCount = 1,
            ValueCount = 1,
            NumericValueCount = 1,
            Sum = 7m,
            Min = 7m,
            Max = 7m,
            DirectProjection = DirectPin(boundary, source)
        };
        row.Id = ActualFixture.P9OwnerStableObjectId(
            boundary.GenerationId,
            "FIELD_AGGREGATE",
            row.WorkId,
            row.ScopeType,
            row.ScopeId,
            row.DynamicFormTemplateId,
            row.FieldId,
            row.BucketKey,
            row.PeriodInstanceKey,
            row.ReportStatus.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return row;
    }

    private static WorkReportDirectProjectionPin DirectPin(
        ActualDirectProjectionBoundary boundary,
        ActualSourceOwnerRevision source)
        => new()
        {
            RunId = boundary.RunId,
            GenerationId = boundary.GenerationId,
            LifecycleEventKey = boundary.OwnerLifecycleEventKey,
            SourceReportId = source.ReportId,
            SourcePayloadRevision = source.PayloadRevision,
            SourcePayloadHash = source.PayloadSha256,
            SourceLifecycleRevision = source.LifecycleRevision,
            DirectSourceRevision = boundary.DirectSourceRevision,
            DynamicFormFamilyId = boundary.DynamicFormFamilyId,
            DynamicFormTemplateId = boundary.DynamicFormTemplateId,
            DynamicFormVersionNo = boundary.DynamicFormVersionNo,
            DynamicFormSchemaHash = boundary.DynamicFormSchemaSha256,
            ConfigId = boundary.ConfigId,
            ConfigVersionId = boundary.ConfigVersionId,
            ConfigVersionNo = boundary.ConfigVersionNo,
            ConfigRevision = boundary.ConfigRevision,
            ConfigHash = boundary.ConfigSha256,
            CandidateChainId = boundary.CandidateChainId,
            CatalogVersion = boundary.CatalogVersion,
            CatalogRawSha256 = boundary.CatalogRawSha256,
            CatalogSemanticSha256 = boundary.CatalogSemanticSha256,
            SchemaRawSha256 = boundary.SchemaRawSha256,
            SchemaSemanticSha256 = boundary.SchemaSemanticSha256,
            StageLockSha256 = boundary.StageLockSha256,
            SourceMembershipSignature = boundary.OwnerMembershipSignature,
            ComputedAtUtc = boundary.OwnerComputedAtUtc
        };

    private static ActualAggregateStoreDigestPin Digest<T>(
        string store,
        IReadOnlyList<T> rows)
    {
        var canonical = rows.Select(row =>
            {
                var document = row!.ToBsonDocument();
                foreach (var name in new[]
                         {
                             "createdAtUtc", "updatedAtUtc",
                             "createdByUserId", "updatedByUserId"
                         })
                    document.Remove(name);
                var id = document["_id"].ToString();
                var json = Canonical(document).AsBsonDocument.ToJson(
                    new JsonWriterSettings
                    {
                        OutputMode = JsonOutputMode.CanonicalExtendedJson,
                        Indent = false
                    });
                return (Id: id, Json: json);
            })
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Json)
            .ToArray();
        var material = $"P9_DIRECT_STORE_ROWS_V2\n{string.Join("\n", canonical)}";
        return new ActualAggregateStoreDigestPin(
            store,
            canonical.LongLength,
            HashBytes(Encoding.UTF8.GetBytes(material)));
    }

    private static BsonValue Canonical(BsonValue value)
    {
        if (value.IsBsonDocument)
            return new BsonDocument(value.AsBsonDocument.Elements
                .OrderBy(element => element.Name, StringComparer.Ordinal)
                .Select(element =>
                    new BsonElement(element.Name, Canonical(element.Value))));
        return value.IsBsonArray
            ? new BsonArray(value.AsBsonArray.Select(Canonical))
            : value;
    }

    private static StatisticReconciliationActualCoherentBoundary CoherentBoundary(
        string sourceSetSha256,
        string configurationSha256,
        string filterSha256,
        string authorizationSha256)
        => StatisticReconciliationActualCoherentCaptureCoordinator.CreateBoundary(
            "reconciliation-full-service",
            ActualFixture.WorkId,
            ActualFixture.Assignment1,
            sourceSetSha256,
            configurationSha256,
            filterSha256,
            authorizationSha256,
            [
                Pin(StatisticReconciliationActualBoundaryDomains.Source, "source", 1),
                Pin(StatisticReconciliationActualBoundaryDomains.Configuration, "config", 2),
                Pin(StatisticReconciliationActualBoundaryDomains.Catalog, "catalog", 3),
                Pin(StatisticReconciliationActualBoundaryDomains.Runtime, "runtime", 4),
                Pin(StatisticReconciliationActualBoundaryDomains.Result, "result", 5),
                Pin(StatisticReconciliationActualBoundaryDomains.Export, "export", 6)
            ]);

    private static StatisticReconciliationActualBoundaryPin Pin(
        string domain,
        string owner,
        long revision)
        => new(domain, owner, "generation-full-service", revision, Hash(owner));

    private static StatisticReconciliationActualExportArtifact ExportArtifact(
        StatisticReconciliationActualCoherentBoundary boundary)
    {
        var text =
            "\"ordinal\",\"amount\",\"totalRows\"\r\n" +
            "\"1\",\"7\",\"1\"\r\n";
        var payload = Encoding.UTF8.GetBytes(text);
        var preamble = Encoding.UTF8.GetPreamble();
        var content = new byte[preamble.Length + payload.Length];
        Buffer.BlockCopy(preamble, 0, content, 0, preamble.Length);
        Buffer.BlockCopy(payload, 0, content, preamble.Length, payload.Length);
        var columns = ImmutableArray.Create(
            new StatisticReconciliationActualExportColumnContract(
                0, "ordinal", "INTEGER", "FORBIDDEN"),
            new StatisticReconciliationActualExportColumnContract(
                1, "amount", "DECIMAL", "FORBIDDEN"),
            new StatisticReconciliationActualExportColumnContract(
                2, "totalRows", "DECIMAL", "FORBIDDEN", true));
        var manifest = new StatisticReconciliationActualExportManifest(
            StatisticReconciliationActualExportParser.RequiredSchemaVersion,
            "export-full-service",
            Hash("export-request"),
            Hash("export-requestor-authorization"),
            "CSV",
            "DIRECT_FIELD",
            "text/csv; charset=utf-8",
            "full-service.csv",
            HashBytes(content),
            content.LongLength,
            1,
            columns.Length,
            boundary.WorkId,
            "ASSIGNMENT",
            boundary.ScopeAssignmentId,
            "result-full-service",
            Hash("result-full-service"),
            Hash("p9-export-config-owner"),
            Hash("p9-export-source-owner"),
            boundary.FilterSha256,
            1,
            "v1.6",
            Hash("catalog-raw"),
            Hash("catalog-semantic"),
            Hash("stage-lock"),
            "p10-chain-full-service",
            "P10-03",
            3,
            Hash("owner-semantic"),
            Utc(12),
            columns,
            ActualFixture.PeriodInstanceKey,
            "{}");
        var parser = new StatisticReconciliationActualExportParser();
        return new StatisticReconciliationActualExportArtifact(
            manifest,
            parser.ComputeManifestSha256(manifest),
            content);
    }

    private static StatisticReconciliationActualPublicationContext
        PublicationContext(StatisticReconciliationActualCoherentBoundary boundary)
    {
        var pins = PublicationPins();
        return new StatisticReconciliationActualPublicationContext(
            boundary.ReconciliationId,
            Hash("immutable-identity-full-service"),
            Hash("immutable-header-full-service"),
            boundary.WorkId,
            boundary.ScopeAssignmentId,
            ActualFixture.PeriodInstanceKey,
            "2026-08",
            "concept-full-service",
            "MONTH",
            "PERIOD_END",
            boundary.FilterSha256,
            ActualFixture.ConfigVersionId,
            ActualFixture.Hash('4'),
            "flow-template-full-service",
            Hash("flow-payload-full-service"),
            "flow-instance-full-service",
            "epoch-full-service",
            3,
            9,
            "p8-owner-full-service",
            Hash("p8-config-bundle-full-service"),
            boundary.ConfigurationBundleSha256,
            boundary.CatalogPinSetSha256,
            pins);
    }

    private static StatisticReconciliationActualPublicationCatalogPins
        PublicationPins()
    {
        var values = new[]
        {
            "v1.6", Hash("p9-catalog-raw-full"), Hash("p9-catalog-sem-full"),
            Hash("p9-schema-raw-full"), Hash("p9-schema-sem-full"),
            Hash("p9-stage-full"), "p10-chain-full", "P10-03",
            "v1.7-candidate", Hash("candidate-catalog-raw-full"),
            Hash("candidate-catalog-sem-full"), Hash("candidate-schema-raw-full"),
            Hash("candidate-schema-sem-full"), Hash("candidate-stage-full")
        };
        var pinSet = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CATALOG_PIN_SET_V1",
            values);
        return new StatisticReconciliationActualPublicationCatalogPins(
            values[0], values[1], values[2], values[3], values[4], values[5],
            values[6], values[7], values[8], values[9], values[10], values[11],
            values[12], values[13], pinSet);
    }

    private static T InvokeFixture<T>(string name, params object?[] arguments)
    {
        var assembly = Assembly.Load("tdtd-be.P10T20T21Tests");
        var fixture = assembly.GetType("Fixture", throwOnError: true)!;
        var methods = fixture.GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
            .ToArray();
        var method = methods.Single(candidate =>
            candidate.GetParameters().Length == arguments.Length);
        return (T)(method.Invoke(null, arguments)
                   ?? throw new InvalidOperationException($"{name}_NULL"));
    }

    private static MeResponse Actor()
        => new(
            "000000000000000000000001",
            "p10-worker",
            "P10 Worker",
            ["SYSTEM"],
            "000000000000000000000002",
            "SYS",
            "System",
            "SYS",
            ["SYSTEM_ADMIN"],
            "ADMIN",
            false,
            "SYSTEM");

    private static DateTime Utc(int day)
        => new(2026, 8, day, 0, 0, 0, DateTimeKind.Utc);

    private static string Hash(string value)
        => HashBytes(Encoding.UTF8.GetBytes(value));

    private static string HashBytes(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
    }
}

internal sealed class FullBoundaryFactory(
    StatisticReconciliationActualCoherentBoundary boundary)
    : IStatisticReconciliationActualCoherentBoundaryReaderFactory
{
    public IStatisticReconciliationActualCoherentBoundaryReader Create(
        StatisticReconciliationActualMongoBoundaryDescriptor descriptor)
        => new FullBoundaryReader(boundary);
}

internal sealed class FullBoundaryReader(
    StatisticReconciliationActualCoherentBoundary boundary)
    : IStatisticReconciliationActualCoherentBoundaryReader
{
    public Task<StatisticReconciliationActualCoherentBoundary> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(boundary);
    }
}

internal sealed class FullAggregateReader(
    IReadOnlyList<WorkReportFieldStatAggregate> fields,
    IReadOnlyList<WorkReportTableStatAggregate> tables,
    IReadOnlyList<WorkReportLabelStatAggregate> labels)
    : IStatisticReconciliationActualAggregateOwnerReader
{
    public Task<IReadOnlyList<WorkReportFieldStatAggregate>>
        ReadFieldGenerationAsync(
            ActualAggregatePublicationBoundary boundary,
            CancellationToken cancellationToken)
        => Task.FromResult(fields);

    public Task<IReadOnlyList<WorkReportTableStatAggregate>>
        ReadTableMetricGenerationAsync(
            ActualAggregatePublicationBoundary boundary,
            CancellationToken cancellationToken)
        => Task.FromResult(tables);

    public Task<IReadOnlyList<WorkReportLabelStatAggregate>>
        ReadRowLabelGenerationAsync(
            ActualAggregatePublicationBoundary boundary,
            CancellationToken cancellationToken)
        => Task.FromResult(labels);
}

internal sealed class FullBasicReader(WorkAssignmentBasicSummarySnapshot owner)
    : IStatisticReconciliationActualBasicOwnerReader
{
    public Task<WorkAssignmentBasicSummarySnapshot?> ReadSnapshotAsync(
        ActualBasicOwnerBoundary boundary,
        CancellationToken cancellationToken)
        => Task.FromResult<WorkAssignmentBasicSummarySnapshot?>(owner);
}

internal sealed class FullAdvancedReader(
    IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode> days,
    IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode> months,
    IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode> years)
    : IStatisticReconciliationActualAdvancedOwnerReader
{
    public Task<IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode>>
        ReadDayNodesAsync(
            ActualAdvancedOwnerBoundary boundary,
            CancellationToken cancellationToken)
        => Task.FromResult(days);

    public Task<IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode>>
        ReadMonthNodesAsync(
            ActualAdvancedOwnerBoundary boundary,
            CancellationToken cancellationToken)
        => Task.FromResult(months);

    public Task<IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode>>
        ReadYearNodesAsync(
            ActualAdvancedOwnerBoundary boundary,
            CancellationToken cancellationToken)
        => Task.FromResult(years);
}

internal sealed class FullDiffReader(WorkReportStatisticDiffResult owner)
    : IStatisticReconciliationActualP9DiffOwnerReader
{
    public Task<WorkReportStatisticDiffResult?> ReadResultAsync(
        ActualP9DiffOwnerBoundary boundary,
        CancellationToken cancellationToken)
        => Task.FromResult<WorkReportStatisticDiffResult?>(owner);
}

internal sealed class FullApiOwnerReader
    : IStatisticReconciliationActualApiOwnerReader
{
    internal bool Drift { get; set; }

    public Task<StatisticReconciliationActualApiAuthorizationContext>
        AuthorizeAsync(
            StatisticReconciliationActualApiAuthorizationProbe probe,
            CancellationToken cancellationToken)
    {
        var permissions = ImmutableArray.Create("STATISTICS_READ");
        var sha = StatisticReconciliationActualApiObservationAdapter
            .AuthorizationSha(
                "service-actor",
                probe.WorkId!,
                probe.ScopeAssignmentId!,
                permissions,
                1,
                1);
        return Task.FromResult(
            new StatisticReconciliationActualApiAuthorizationContext(
                "service-actor",
                probe.WorkId!,
                probe.ScopeAssignmentId!,
                permissions,
                1,
                1,
                sha,
                true));
    }

    public Task<StatisticReconciliationActualApiOwnerPage> ReadPageAsync(
        StatisticReconciliationActualApiAuthorizationContext authorization,
        StatisticReconciliationActualApiOwnerPageQuery query,
        CancellationToken cancellationToken)
    {
        var rowJson = Drift
            ? "{\"amount\":8,\"id\":\"row-001\"}"
            : "{\"amount\":7,\"id\":\"row-001\"}";
        var row = new StatisticReconciliationActualApiOwnerRow(
            "row-001",
            rowJson,
            StatisticReconciliationActualApiObservationAdapter.RowSha(
                "row-001",
                rowJson));
        return Task.FromResult(new StatisticReconciliationActualApiOwnerPage(
            query.Surface,
            query.RouteId,
            query.WorkId,
            query.ScopeAssignmentId,
            query.DynamicFormTemplateId,
            query.OwnerResultId,
            query.FilterSha256,
            query.AuthorizationSnapshotSha256,
            query.RequestSha256,
            "\"full-service-etag\"",
            FullHash("api-generation-id"),
            FullHash("api-generation-sha"),
            query.Page,
            query.PageSize,
            1,
            1,
            [new("totalRows", "INTEGER", "1")],
            [row]));
    }

    private static string FullHash(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

internal sealed class FullExportOwnerReader(
    StatisticReconciliationActualExportOwnerTarget expected,
    StatisticReconciliationActualExportArtifact artifact)
    : IStatisticReconciliationActualExportOwnerReader
{
    internal Action? BeforeNextRead { get; set; }

    public Task<StatisticReconciliationActualExportOwnerRead> ReadArtifactAsync(
        StatisticReconciliationActualExportOwnerTarget target,
        CancellationToken cancellationToken = default)
    {
        var beforeRead = BeforeNextRead;
        BeforeNextRead = null;
        beforeRead?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (!Equals(target, expected))
            return Task.FromResult(
                StatisticReconciliationActualExportOwnerRead.Stale(
                    "EXPORT_TARGET_MISMATCH"));
        return Task.FromResult(
            StatisticReconciliationActualExportOwnerRead.Ready(artifact));
    }
}
