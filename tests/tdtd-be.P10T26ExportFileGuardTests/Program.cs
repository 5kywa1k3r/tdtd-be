using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

var cases = new (string Id, ExportFileChange Change, string Reason)[]
{
    ("P10-T26-EXPORT-GUARD-01", ExportFileChange.Mutate,
        "EXPORT_ARTIFACT_DRIFT"),
    ("P10-T26-EXPORT-GUARD-02", ExportFileChange.Delete,
        "EXPORT_ARTIFACT_MISSING")
};

foreach (var item in cases)
{
    try
    {
        await ExportFileChangeAfterLayerIsStaleAsync(item.Change, item.Reason);
        Console.WriteLine($"PASS {item.Id}");
    }
    catch (Exception error)
    {
        Console.WriteLine(
            $"FAIL {item.Id} {error.GetType().Name}:{error.Message}");
        return 1;
    }
}

Console.WriteLine(
    "P10_T26_EXPORT_FILE_GUARD_OK cases=2 mutatedStale=true " +
    "deletedStale=true appendZero=true casZero=true");
return 0;

static async Task ExportFileChangeAfterLayerIsStaleAsync(
    ExportFileChange change,
    string expectedReason)
{
    await using var artifact = await GuardedArtifact.CreateAsync();
    var boundary = Boundary();
    var backend = new CountingBackend();
    var cas = new CountingCas();
    var layers = new List<string>();
    var gate = new StatisticReconciliationActualCapturePublicationGate(
        new StatisticReconciliationActualGenerationPublisher(backend, cas));

    var outcome = await gate.ExecuteAsync(
        boundary.ReconciliationId,
        new StableBoundaryReader(boundary),
        Steps(boundary, layers, () => artifact.Apply(change)),
        Context(boundary),
        Utc(),
        "worker-export-guard",
        "claim-export-guard",
        Actor(),
        finalGuardAsync: artifact.ReadGuardReasonAsync);

    Require(layers.SequenceEqual(
            StatisticReconciliationActualCoherentLayers.RequiredOrder,
            StringComparer.Ordinal),
        "ALL_LAYERS_CAPTURED_BEFORE_FINAL_EXPORT_GUARD");
    Require(!outcome.Published && outcome.Publication is null,
        "EXPORT_CHANGE_MUST_FORBID_PUBLICATION");
    Require(outcome.Generation.CaptureState ==
            StatisticReconciliationActualCoherentCaptureStates.Stale,
        "EXPORT_CHANGE_MUST_BE_STALE");
    Require(outcome.Generation.StaleReason == $"FINAL_GUARD:{expectedReason}",
        "EXACT_EXPORT_FINAL_GUARD_REASON");
    Require(!outcome.Generation.ReadyForAppendOnlyPersistence &&
            outcome.Generation.PublicationForbidden,
        "STALE_EXPORT_NEVER_PERSISTENCE_READY");
    Require(backend.ReadCalls == 0 && backend.ContentWrites == 0 &&
            backend.CommitWrites == 0 && cas.PublishCalls == 0,
        "STALE_EXPORT_ZERO_READ_APPEND_COMMIT_CAS");
}

static ImmutableArray<StatisticReconciliationActualCoherentCaptureStep> Steps(
    StatisticReconciliationActualCoherentBoundary boundary,
    List<string> layers,
    Action afterExportCapture)
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
                    layers.Add(layer);
                    var capture = new StatisticReconciliationActualLayerCapture(
                        layer,
                        boundary.BoundarySemanticSha256,
                        Hash($"capture-{ordinal}"),
                        1,
                        StatisticReconciliationActualCoherentCaptureStates.Ready,
                        null,
                        ownerId,
                        ownerVersion,
                        typed);
                    if (layer ==
                        StatisticReconciliationActualCoherentLayers.Export)
                    {
                        afterExportCapture();
                    }
                    return Task.FromResult(capture);
                });
        })
        .ToImmutableArray();

static StatisticReconciliationActualCoherentBoundary Boundary()
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
            "result-owner", 17, Hash("result")),
        Pin(StatisticReconciliationActualBoundaryDomains.Export,
            "export-owner", 8, Hash("export"))
    };
    return StatisticReconciliationActualCoherentCaptureCoordinator.CreateBoundary(
        "reconciliation-export-guard",
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

static DateTime Utc()
    => new(2026, 8, 11, 4, 0, 0, DateTimeKind.Utc);

static string Hash(string value)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

static void Require(bool condition, string reason)
{
    if (!condition)
        throw new InvalidOperationException(reason);
}

internal enum ExportFileChange
{
    Mutate,
    Delete
}

internal sealed class GuardedArtifact : IAsyncDisposable
{
    private readonly string _directory;
    private readonly string _expectedSha256;

    private GuardedArtifact(string directory, string path, string expectedSha256)
    {
        _directory = directory;
        Path = path;
        _expectedSha256 = expectedSha256;
    }

    internal string Path { get; }

    internal static async Task<GuardedArtifact> CreateAsync()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"tdtd-p10-export-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "artifact.bin");
        var bytes = Encoding.UTF8.GetBytes("immutable-export-bytes");
        await File.WriteAllBytesAsync(path, bytes);
        return new GuardedArtifact(
            directory,
            path,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    internal void Apply(ExportFileChange change)
    {
        if (change == ExportFileChange.Delete)
        {
            File.Delete(Path);
            return;
        }
        File.WriteAllBytes(Path, Encoding.UTF8.GetBytes("mutated-export-bytes"));
    }

    internal async Task<string?> ReadGuardReasonAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(Path))
            return "EXPORT_ARTIFACT_MISSING";
        var bytes = await File.ReadAllBytesAsync(Path, cancellationToken);
        var actual = Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();
        return actual == _expectedSha256 ? null : "EXPORT_ARTIFACT_DRIFT";
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }
}

internal sealed class StableBoundaryReader(
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

internal sealed class CountingBackend
    : IStatisticReconciliationActualObservationBackend
{
    internal int ReadCalls { get; private set; }
    internal int ContentWrites { get; private set; }
    internal int CommitWrites { get; private set; }

    public Task<IReadOnlyList<StatisticReconciliationObservation>>
        ReadGenerationAsync(
            string reconciliationId,
            string generationId,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCalls++;
        return Task.FromResult<IReadOnlyList<StatisticReconciliationObservation>>(
            []);
    }

    public Task AppendContentAsync(
        IReadOnlyList<StatisticReconciliationObservation> observations,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ContentWrites++;
        return Task.CompletedTask;
    }

    public Task AppendCommitAsync(
        StatisticReconciliationObservation observation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitWrites++;
        return Task.CompletedTask;
    }
}

internal sealed class CountingCas : IStatisticReconciliationActualGenerationCas
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
        return Task.CompletedTask;
    }
}
