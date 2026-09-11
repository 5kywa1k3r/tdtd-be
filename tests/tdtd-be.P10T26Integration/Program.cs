using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

var cases = new (string Id, Func<Task> Run)[]
{
    ("P10-T26-INT-01", ReadyEightLayersPublishesOnceAsync),
    ("P10-T26-INT-02", BoundaryDriftNeverPublishesAsync),
    ("P10-T26-INT-03", ApiAuthorizationPrecedesPageAsync),
    ("P10-T26-INT-04", HttpApiOwnerIntegration.RunAsync),
    ("P10-T26-INT-05", ExportSidecarContractIntegration.RunAsync)
};

foreach (var item in cases)
{
    try
    {
        await item.Run();
        Console.WriteLine($"PASS {item.Id}");
    }
    catch (Exception error)
    {
        Console.WriteLine($"FAIL {item.Id} {error.GetType().Name}:{error.Message}");
        return 1;
    }
}

Console.WriteLine(
    "P10_T26_INTEGRATION_OK cases=5 layers=8 driftNoPublish=true authBeforeExistence=true readyLayers=8 apiOwner=true exportSidecar=true");
return 0;

static async Task ReadyEightLayersPublishesOnceAsync()
{
    var boundary = Boundary();
    var calls = new List<string>();
    var events = new List<string>();
    var backend = new MemoryBackend(events);
    var cas = new CountingCas(events);
    var gate = new StatisticReconciliationActualCapturePublicationGate(
        new StatisticReconciliationActualGenerationPublisher(backend, cas));

    var outcome = await gate.ExecuteAsync(
        boundary.ReconciliationId,
        new SequenceBoundaryReader(boundary, boundary),
        Steps(boundary, calls),
        Context(boundary),
        Utc(1),
        "worker-integration",
        "claim-integration",
        Actor(),
        default,
        null,
        null,
        PublicationV7Fixture.PrepareAsync);

    Require(outcome.Published, "READY_MUST_PUBLISH");
    Require(calls.SequenceEqual(
        StatisticReconciliationActualCoherentLayers.RequiredOrder,
        StringComparer.Ordinal), "EXACT_EIGHT_LAYER_ORDER");
    Require(backend.ContentWrites == 1 && backend.CommitWrites == 1,
        "ONE_APPEND_SEQUENCE");
    Require(cas.PublishCalls == 1, "ONE_CAS_PUBLICATION");
    Require(events.IndexOf("APPEND_CONTENT") < events.IndexOf("APPEND_COMMIT") &&
            events.IndexOf("APPEND_COMMIT") < events.IndexOf("CAS"),
        "CONTENT_COMMIT_CAS_ORDER");
}

static async Task BoundaryDriftNeverPublishesAsync()
{
    var before = Boundary();
    var after = Boundary(18, Hash("result-drift"));
    var calls = new List<string>();
    var backend = new MemoryBackend([]);
    var cas = new CountingCas([]);
    var gate = new StatisticReconciliationActualCapturePublicationGate(
        new StatisticReconciliationActualGenerationPublisher(backend, cas));

    var outcome = await gate.ExecuteAsync(
        before.ReconciliationId,
        new SequenceBoundaryReader(before, after),
        Steps(before, calls),
        Context(before),
        Utc(2),
        "worker-drift",
        "claim-drift",
        Actor());

    Require(!outcome.Published, "DRIFT_PUBLICATION_FORBIDDEN");
    Require(outcome.Generation.CaptureState ==
            StatisticReconciliationActualCoherentCaptureStates.Stale,
        "DRIFT_IS_STALE");
    Require(outcome.Generation.StaleReason == "BOUNDARY_DRIFT",
        "DRIFT_REASON");
    Require(backend.ContentWrites == 0 && backend.CommitWrites == 0 &&
            cas.PublishCalls == 0,
        "DRIFT_ZERO_WRITE_ZERO_CAS");
    Require(calls.Count == 8, "DRIFT_REREAD_AFTER_ALL_LAYERS");
}

static async Task ApiAuthorizationPrecedesPageAsync()
{
    var events = new List<string>();
    var authorizationSource = new AuthorizationSource(events);
    var pageSource = new PageSource(events);
    var owner = new StatisticReconciliationActualReadOnlyApiOwnerReader(
        authorizationSource,
        pageSource);
    var probe = new StatisticReconciliationActualApiAuthorizationProbe(
        "work-01",
        "scope-01");
    var authorization = await owner.AuthorizeAsync(probe, default);
    var query = new StatisticReconciliationActualApiOwnerPageQuery(
        StatisticReconciliationActualApiSurfaces.DirectField,
        "DIRECT_FIELD_RESULT",
        authorization.WorkId,
        authorization.ScopeAssignmentId,
        "template-01",
        null,
        "{}",
        Hash("filter"),
        authorization.AuthorizationSnapshotSha256,
        0,
        50,
        Hash("request"));
    await owner.ReadPageAsync(authorization, query, default);
    Require(events.SequenceEqual(["AUTH", "PAGE"], StringComparer.Ordinal),
        "AUTH_MUST_PRECEDE_EXISTENCE_QUERY");
}

static ImmutableArray<StatisticReconciliationActualCoherentCaptureStep> Steps(
    StatisticReconciliationActualCoherentBoundary boundary,
    List<string> calls)
    => StatisticReconciliationActualCoherentLayers.RequiredOrder
        .Select((layer, ordinal) =>
        {
            var ownerId = $"owner-{ordinal}";
            var ownerVersion = Hash($"owner-version-{ordinal}");
            var typed = layer ==
                        StatisticReconciliationActualCoherentLayers.DirectProjection
                ? ImmutableArray.Create(
                    StatisticReconciliationActualTypedObservationCanonical.Create(
                        0,
                        layer,
                        ownerId,
                        ownerVersion,
                        "DIRECT",
                        "FIELD",
                        "metric-01",
                        "2026-08",
                        "COUNT",
                        "NUMBER",
                        "1",
                        occurrenceCount: 1,
                        reportCount: 1,
                        fieldId: "field-01"))
                : ImmutableArray<StatisticReconciliationActualTypedObservation>.Empty;
            return new StatisticReconciliationActualCoherentCaptureStep(
                layer,
                (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    calls.Add(layer);
                    return Task.FromResult(
                        new StatisticReconciliationActualLayerCapture(
                            layer,
                            boundary.BoundarySemanticSha256,
                            Hash($"capture-{ordinal}"),
                            1,
                            StatisticReconciliationActualCoherentCaptureStates.Ready,
                            null,
                            ownerId,
                            ownerVersion,
                            typed));
                });
        })
        .ToImmutableArray();

static StatisticReconciliationActualCoherentBoundary Boundary(
    long resultRevision = 17,
    string? resultSemanticSha256 = null)
{
    var pins = new[]
    {
        Pin(StatisticReconciliationActualBoundaryDomains.Source,
            "source-owner", 4, Hash("source")),
        Pin(StatisticReconciliationActualBoundaryDomains.Configuration,
            "configuration-owner", 5, Hash("configuration")),
        Pin(StatisticReconciliationActualBoundaryDomains.Catalog,
            "catalog-owner", 6, Hash("catalog")),
        Pin(StatisticReconciliationActualBoundaryDomains.Runtime,
            "runtime-owner", 7, Hash("runtime")),
        Pin(StatisticReconciliationActualBoundaryDomains.Result,
            "result-owner", resultRevision,
            resultSemanticSha256 ?? Hash("result")),
        Pin(StatisticReconciliationActualBoundaryDomains.Export,
            "export-owner", 8, Hash("export"))
    };
    return StatisticReconciliationActualCoherentCaptureCoordinator.CreateBoundary(
        "reconciliation-01",
        "work-01",
        "scope-01",
        Hash("source-set"),
        Hash("configuration-bundle"),
        Hash("filter"),
        Hash("authorization"),
        pins);
}

static StatisticReconciliationActualBoundaryPin Pin(
    string domain,
    string owner,
    long revision,
    string semanticSha256)
    => new(domain, owner, "p9-generation-17", revision, semanticSha256);

static StatisticReconciliationActualPublicationContext Context(
    StatisticReconciliationActualCoherentBoundary boundary)
{
    var pins = CatalogPins();
    return new StatisticReconciliationActualPublicationContext(
        boundary.ReconciliationId,
        Hash("immutable-identity"),
        Hash("immutable-header"),
        boundary.WorkId,
        boundary.ScopeAssignmentId,
        "month:2026-08",
        "2026-08",
        "concept-01",
        "MONTH",
        "PERIOD_END",
        boundary.FilterSha256,
        "form-version-01",
        Hash("form-schema"),
        "flow-template-01",
        Hash("flow-payload"),
        "flow-instance-01",
        "epoch-01",
        3,
        9,
        "p8-owner-01",
        Hash("p8-config-bundle"),
        boundary.ConfigurationBundleSha256,
        boundary.CatalogPinSetSha256,
        pins);
}

static StatisticReconciliationActualPublicationCatalogPins CatalogPins()
{
    var values = new[]
    {
        "v1.6",
        Hash("p9-catalog-raw"),
        Hash("p9-catalog-semantic"),
        Hash("p9-schema-raw"),
        Hash("p9-schema-semantic"),
        Hash("p9-stage-lock"),
        "p10-chain-01",
        "P10-03",
        "v1.7-candidate",
        Hash("candidate-catalog-raw"),
        Hash("candidate-catalog-semantic"),
        Hash("candidate-schema-raw"),
        Hash("candidate-schema-semantic"),
        Hash("candidate-stage-lock")
    };
    var pinSet = StatisticReconciliationActualCanonical.Hash(
        "P10_ACTUAL_CATALOG_PIN_SET_V1",
        values);
    return new StatisticReconciliationActualPublicationCatalogPins(
        values[0], values[1], values[2], values[3], values[4], values[5],
        values[6], values[7], values[8], values[9], values[10], values[11],
        values[12], values[13], pinSet);
}

static MeResponse Actor()
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

static DateTime Utc(int hour)
    => new(2026, 8, 11, hour, 0, 0, DateTimeKind.Utc);

static string Hash(string value)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

static void Require(bool condition, string reason)
{
    if (!condition)
        throw new InvalidOperationException(reason);
}

internal sealed class SequenceBoundaryReader(
    StatisticReconciliationActualCoherentBoundary first,
    StatisticReconciliationActualCoherentBoundary second)
    : IStatisticReconciliationActualCoherentBoundaryReader
{
    private int _reads;

    public Task<StatisticReconciliationActualCoherentBoundary> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _reads++;
        return Task.FromResult(_reads == 1 ? first : second);
    }
}

internal sealed class MemoryBackend(List<string> events)
    : IStatisticReconciliationActualObservationBackend
{
    private readonly List<StatisticReconciliationObservation> _documents = [];
    internal int ContentWrites { get; private set; }
    internal int CommitWrites { get; private set; }
    internal int ReadCalls { get; private set; }

    public Task<IReadOnlyList<StatisticReconciliationObservation>>
        ReadGenerationAsync(
            string reconciliationId,
            string generationId,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCalls++;
        return Task.FromResult<IReadOnlyList<StatisticReconciliationObservation>>(
            _documents.Where(item =>
                    item.ReconciliationId == reconciliationId &&
                    item.GenerationId == generationId)
                .ToArray());
    }

    public Task AppendContentAsync(
        IReadOnlyList<StatisticReconciliationObservation> observations,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ContentWrites++;
        events.Add("APPEND_CONTENT");
        Append(observations);
        return Task.CompletedTask;
    }

    public Task AppendCommitAsync(
        StatisticReconciliationObservation observation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitWrites++;
        events.Add("APPEND_COMMIT");
        Append([observation]);
        return Task.CompletedTask;
    }

    private void Append(IEnumerable<StatisticReconciliationObservation> values)
    {
        foreach (var value in values)
        {
            if (_documents.All(item => item.Id != value.Id))
                _documents.Add(value);
        }
    }
}

internal sealed class CountingCas(List<string> events)
    : IStatisticReconciliationActualGenerationCas
{
    internal int PublishCalls { get; private set; }

    public Task PublishAsync(
        string reconciliationId,
        string generationId,
        string generationSemanticSha256,
        string workerId,
        string claimToken,
        MeResponse actor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PublishCalls++;
        events.Add("CAS");
        return Task.CompletedTask;
    }
}

internal sealed class AuthorizationSource(List<string> events)
    : IStatisticReconciliationActualApiAuthorizationSource
{
    public Task<StatisticReconciliationActualApiAuthorizationContext>
        AuthorizeBeforeExistenceAsync(
            StatisticReconciliationActualApiAuthorizationProbe probe,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        events.Add("AUTH");
        var permissions = ImmutableArray.Create("STATISTICS_READ");
        var sha = StatisticReconciliationActualApiObservationAdapter
            .AuthorizationSha(
                "actor-01",
                probe.WorkId!,
                probe.ScopeAssignmentId!,
                permissions,
                10,
                10);
        return Task.FromResult(
            new StatisticReconciliationActualApiAuthorizationContext(
                "actor-01",
                probe.WorkId!,
                probe.ScopeAssignmentId!,
                permissions,
                10,
                10,
                sha,
                true));
    }
}

internal sealed class PageSource(List<string> events)
    : IStatisticReconciliationActualApiPageProjectionSource
{
    public Task<StatisticReconciliationActualApiOwnerPage>
        ReadPageProjectionAsync(
            StatisticReconciliationActualApiAuthorizationContext authorization,
            StatisticReconciliationActualApiOwnerPageQuery query,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!events.SequenceEqual(["AUTH"], StringComparer.Ordinal))
            throw new InvalidOperationException("EXISTENCE_READ_BEFORE_AUTH");
        events.Add("PAGE");
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
            null,
            HashForPage("generation-id"),
            HashForPage("generation-sha"),
            query.Page,
            query.PageSize,
            0,
            0,
            [],
            []));
    }

    private static string HashForPage(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
