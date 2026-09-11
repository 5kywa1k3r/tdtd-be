using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

var cases = new (string Id, Func<Task> Run)[]
{
    ("P10-T24-01", CoherentEightLayerGenerationAsync),
    ("P10-T24-02", FinalBoundaryDriftIsStaleAsync),
    ("P10-T24-03", MixedLayerBoundaryIsStaleAsync),
    ("P10-T24-04", FailClosedPlanAndLayerContractsAsync)
};

var passed = 0;
foreach (var item in cases)
{
    try
    {
        await item.Run();
        Console.WriteLine($"PASS {item.Id}");
        passed++;
    }
    catch (Exception error)
    {
        Console.WriteLine($"FAIL {item.Id} {error}");
        return 1;
    }
}

Require(passed == 4, "EXACT_CASE_COUNT");
Console.WriteLine("P10_T24_COHERENT_CAPTURE_OK cases=4 stopBefore=P10-T25");
return 0;

static async Task CoherentEightLayerGenerationAsync()
{
    var boundary = Boundary();
    var reader = new BoundaryReader(boundary, boundary);
    var executionOrder = new List<string>();
    var coordinator = new StatisticReconciliationActualCoherentCaptureCoordinator();
    var first = await coordinator.CaptureAsync(
        boundary.ReconciliationId,
        reader,
        Steps(boundary, executionOrder));

    Require(first.CaptureState ==
            StatisticReconciliationActualCoherentCaptureStates.Ready,
        "COHERENT_READY");
    Require(first.BoundaryStable, "BOUNDARY_STABLE");
    Require(first.Complete, "GENERATION_COMPLETE");
    Require(first.ReadyForAppendOnlyPersistence, "PERSISTENCE_ELIGIBLE");
    Require(!first.PublicationForbidden, "PUBLICATION_NOT_FORBIDDEN");
    Require(first.MatchAndSignOffForbidden, "T24_NEVER_GRANTS_MATCH_OR_SIGNOFF");
    Require(reader.ReadCount == 2, "BOUNDARY_READ_TWICE");
    Require(executionOrder.SequenceEqual(
        StatisticReconciliationActualCoherentLayers.RequiredOrder,
        StringComparer.Ordinal), "EXACT_LAYER_EXECUTION_ORDER");
    Require(first.OrderedLayerObservations.Length == 8,
        "EXACT_LAYER_OBSERVATIONS");
    Require(first.OrderedLayerObservations.Select(item => item.Ordinal)
        .SequenceEqual(Enumerable.Range(0, 8)), "CANONICAL_ORDINALS");
    Require(first.OrderedLayerObservations.All(item =>
        item.BoundaryMatches &&
        item.CaptureState ==
        StatisticReconciliationActualCoherentCaptureStates.Ready),
        "ALL_LAYERS_BOUND_READY");

    var shuffledBoundary = Boundary(reversePhysicalPins: true);
    Require(
        shuffledBoundary.BoundarySemanticSha256 ==
        boundary.BoundarySemanticSha256 &&
        shuffledBoundary.Pins.SequenceEqual(boundary.Pins),
        "PIN_PHYSICAL_ORDER_INDEPENDENT");
    var replay = await coordinator.CaptureAsync(
        boundary.ReconciliationId,
        new BoundaryReader(shuffledBoundary, shuffledBoundary),
        Steps(shuffledBoundary, new List<string>()));
    Require(replay.GenerationId == first.GenerationId,
        "DETERMINISTIC_GENERATION_ID");
    Require(replay.GenerationSemanticSha256 == first.GenerationSemanticSha256,
        "DETERMINISTIC_GENERATION_SEMANTIC");
    Require(replay.LayerManifestSha256 == first.LayerManifestSha256,
        "DETERMINISTIC_LAYER_MANIFEST");
}

static async Task FinalBoundaryDriftIsStaleAsync()
{
    var before = Boundary();
    var after = Boundary(resultRevision: 18, resultSemantic: Hash("result-18"));
    var reader = new BoundaryReader(before, after);
    var executionOrder = new List<string>();
    var result = await new StatisticReconciliationActualCoherentCaptureCoordinator()
        .CaptureAsync(
            before.ReconciliationId,
            reader,
            Steps(before, executionOrder));

    Require(result.CaptureState ==
            StatisticReconciliationActualCoherentCaptureStates.Stale,
        "DRIFT_STALE");
    Require(result.StaleReason == "BOUNDARY_DRIFT", "DRIFT_REASON");
    Require(!result.BoundaryStable, "DRIFT_BOUNDARY_UNSTABLE");
    Require(result.Complete, "DRIFT_CAPTURE_COMPLETED_BEFORE_REREAD");
    Require(!result.ReadyForAppendOnlyPersistence, "STALE_NOT_PERSISTENCE_ELIGIBLE");
    Require(result.PublicationForbidden, "STALE_PUBLICATION_FORBIDDEN");
    Require(result.MatchAndSignOffForbidden, "STALE_MATCH_SIGNOFF_FORBIDDEN");
    Require(reader.ReadCount == 2, "DRIFT_REREAD_PERFORMED");
    Require(executionOrder.Count == 8, "ALL_LAYERS_CAPTURED_BEFORE_FINAL_REREAD");
    Require(result.CommonBoundary.BoundarySemanticSha256 !=
            result.FinalBoundary.BoundarySemanticSha256,
        "DRIFT_EVIDENCE_RETAINED");
}

static async Task MixedLayerBoundaryIsStaleAsync()
{
    var boundary = Boundary();
    var other = Boundary(resultRevision: 99, resultSemantic: Hash("other-result"));
    var order = new List<string>();
    var steps = Steps(
        boundary,
        order,
        mixedLayer: StatisticReconciliationActualCoherentLayers.Aggregate,
        mixedBoundarySha256: other.BoundarySemanticSha256);
    var reader = new BoundaryReader(boundary, boundary);
    var result = await new StatisticReconciliationActualCoherentCaptureCoordinator()
        .CaptureAsync(boundary.ReconciliationId, reader, steps);

    Require(result.CaptureState ==
            StatisticReconciliationActualCoherentCaptureStates.Stale,
        "MIXED_LAYER_STALE");
    Require(result.StaleReason == "LAYER_BOUNDARY_MISMATCH:AGGREGATE",
        "MIXED_LAYER_REASON");
    Require(result.BoundaryStable, "OWNER_BOUNDARY_ITSELF_STABLE");
    Require(!result.Complete, "MIXED_CAPTURE_STOPS_EARLY");
    Require(result.OrderedLayerObservations.Length == 3,
        "MIXED_CAPTURE_EXACT_PREFIX");
    Require(!result.OrderedLayerObservations[^1].BoundaryMatches,
        "MIXED_LAYER_RECORDED");
    Require(reader.ReadCount == 2, "MIXED_LAYER_FINAL_REREAD");
    Require(result.PublicationForbidden && result.MatchAndSignOffForbidden,
        "MIXED_LAYER_HARD_BLOCK");
}

static async Task FailClosedPlanAndLayerContractsAsync()
{
    var boundary = Boundary();
    var coordinator = new StatisticReconciliationActualCoherentCaptureCoordinator();
    var missing = Steps(boundary, new List<string>()).RemoveAt(7);
    await ExpectReasonAsync(
        () => coordinator.CaptureAsync(
            boundary.ReconciliationId,
            new BoundaryReader(boundary, boundary),
            missing),
        "COHERENT_LAYER_SET_INVALID");

    var reordered = Steps(boundary, new List<string>()).ToBuilder();
    (reordered[0], reordered[1]) = (reordered[1], reordered[0]);
    await ExpectReasonAsync(
        () => coordinator.CaptureAsync(
            boundary.ReconciliationId,
            new BoundaryReader(boundary, boundary),
            reordered.ToImmutable()),
        "COHERENT_LAYER_ORDER_INVALID");

    var staleSteps = Steps(
        boundary,
        new List<string>(),
        staleLayer: StatisticReconciliationActualCoherentLayers.Api);
    var staleReader = new BoundaryReader(boundary, boundary);
    var stale = await coordinator.CaptureAsync(
        boundary.ReconciliationId,
        staleReader,
        staleSteps);
    Require(stale.CaptureState ==
            StatisticReconciliationActualCoherentCaptureStates.Stale,
        "LAYER_REPORTED_STALE");
    Require(stale.StaleReason == "LAYER_STALE:API:API_GENERATION_DRIFT",
        "LAYER_STALE_REASON_BOUND");
    Require(stale.OrderedLayerObservations.Length == 7,
        "LAYER_STALE_STOPS_BEFORE_EXPORT");
    Require(staleReader.ReadCount == 2, "LAYER_STALE_FINAL_REREAD");
    Require(stale.PublicationForbidden, "NO_T25_SIDE_EFFECT");

    var empty = await coordinator.CaptureAsync(
        boundary.ReconciliationId,
        new BoundaryReader(boundary, boundary),
        Steps(boundary, new List<string>(), emptyTyped: true));
    Require(empty.CaptureState ==
            StatisticReconciliationActualCoherentCaptureStates.Ready &&
            !empty.ReadyForAppendOnlyPersistence &&
            empty.PublicationForbidden,
        "NO_COMPARABLE_TYPED_OBSERVATION_NOT_PERSISTENCE_READY");

    BoundaryDescriptorControls();
    await BoundaryAuthorizationControlsAsync();
    await BoundaryPreQueryControlsAsync();
}

static void BoundaryDescriptorControls()
{
    var valid = new StatisticReconciliationActualMongoBoundarySlice(
        StatisticReconciliationActualBoundaryDomains.Source,
        "work_assignment_report",
        "scope-01",
        "p9-generation-17",
        1,
        new BsonDocument("workId", "work-01"),
        ["workId", "revision"]);
    var normalized =
        StatisticReconciliationActualMongoBoundaryReader.NormalizeSlices([valid]);
    Require(normalized.Length == 1 &&
            normalized[0].ProjectionFields.SequenceEqual(
                new[] { "revision", "workId" },
                StringComparer.Ordinal) &&
            !ReferenceEquals(normalized[0].Filter, valid.Filter),
        "BOUNDARY_VALID_NORMALIZATION");

    ExpectMongoReason(
        () => StatisticReconciliationActualMongoBoundaryReader.NormalizeSlices(
            Enumerable.Repeat(
                    valid,
                    StatisticReconciliationActualMongoBoundaryReader.MaxSlices + 1)
                .ToImmutableArray()),
        "ACTUAL_MONGO_BOUNDARY_SLICE_COUNT_LIMIT");

    ExpectMongoReason(
        () => StatisticReconciliationActualMongoBoundaryReader.Normalize(
            valid with
            {
                Collection = "users"
            }),
        "ACTUAL_MONGO_BOUNDARY_SLICE_COLLECTION_NOT_ALLOWED");
    ExpectMongoReason(
        () => StatisticReconciliationActualMongoBoundaryReader.Normalize(
            valid with
            {
                Domain = StatisticReconciliationActualBoundaryDomains.Export
            }),
        "ACTUAL_MONGO_BOUNDARY_SLICE_COLLECTION_NOT_ALLOWED");
    ExpectMongoReason(
        () => StatisticReconciliationActualMongoBoundaryReader.Normalize(
            valid with
            {
                ProjectionFields = Enumerable.Range(
                        0,
                        StatisticReconciliationActualMongoBoundaryReader
                            .MaxProjectionFields + 1)
                    .Select(value => $"field{value}")
                    .ToImmutableArray()
            }),
        "ACTUAL_MONGO_BOUNDARY_SLICE_PROJECTION_LIMIT");

    var forbiddenFilters = new[]
    {
        new BsonDocument("$where", "true"),
        new BsonDocument(
            "nested",
            new BsonDocument("$regex", "^unsafe")),
        new BsonDocument(
            "$and",
            new BsonArray
            {
                new BsonDocument(
                    "$expr",
                    new BsonDocument(
                        "$eq",
                        new BsonArray { 1, 1 }))
            }),
        new BsonDocument("value", new BsonRegularExpression("^unsafe"))
    };
    foreach (var filter in forbiddenFilters)
    {
        ExpectMongoReason(
            () => StatisticReconciliationActualMongoBoundaryReader
                .NormalizeFilter(filter),
            "ACTUAL_MONGO_BOUNDARY_FILTER_OPERATOR_FORBIDDEN");
    }

    BsonValue tooDeep = new BsonDocument("leaf", 1);
    for (var depth = 0;
         depth < StatisticReconciliationActualMongoBoundaryReader.MaxFilterDepth;
         depth++)
    {
        tooDeep = new BsonDocument("nested", tooDeep);
    }
    ExpectMongoReason(
        () => StatisticReconciliationActualMongoBoundaryReader.NormalizeFilter(
            (BsonDocument)tooDeep),
        "ACTUAL_MONGO_BOUNDARY_FILTER_DEPTH_LIMIT");

    var tooManyArrayItems = new BsonArray(
        Enumerable.Range(
                0,
                StatisticReconciliationActualMongoBoundaryReader
                    .MaxFilterArrayElements + 1)
            .Select(value => new BsonInt32(value)));
    ExpectMongoReason(
        () => StatisticReconciliationActualMongoBoundaryReader.NormalizeFilter(
            new BsonDocument("ids", tooManyArrayItems)),
        "ACTUAL_MONGO_BOUNDARY_FILTER_ARRAY_LIMIT");

    ExpectMongoReason(
        () => StatisticReconciliationActualMongoBoundaryReader.NormalizeFilter(
            new BsonDocument(
                "payload",
                new string(
                    'x',
                    StatisticReconciliationActualMongoBoundaryReader
                        .MaxFilterBytes))),
        "ACTUAL_MONGO_BOUNDARY_FILTER_SIZE_LIMIT");

    Require(
        StatisticReconciliationActualMongoBoundaryReader.ReadLimit(0) ==
        StatisticReconciliationActualMongoBoundaryReader
            .MaxDocumentsPerSlice + 1,
        "BOUNDARY_INITIAL_READ_LIMIT");
    Require(
        StatisticReconciliationActualMongoBoundaryReader.ReadLimit(450_000) ==
        50_001,
        "BOUNDARY_REMAINING_TOTAL_READ_LIMIT");
    Require(
        StatisticReconciliationActualMongoBoundaryReader.ReadLimit(
            StatisticReconciliationActualMongoBoundaryReader
                .MaxTotalDocuments) == 1,
        "BOUNDARY_EXHAUSTED_TOTAL_PROBE_LIMIT");
    ExpectMongoReason(
        () => StatisticReconciliationActualMongoBoundaryReader.ReadLimit(
            StatisticReconciliationActualMongoBoundaryReader
                .MaxTotalDocuments + 1),
        "ACTUAL_MONGO_BOUNDARY_TOTAL_DOCUMENT_COUNT_INVALID");
}

static async Task BoundaryAuthorizationControlsAsync()
{
    var initialRun = AuthorizationRun(
        "STAT_RUN_READ",
        "WORK_VIEW");
    var forgedReader = AuthorizationReader(Hash("forged-descriptor-auth"));
    var forgedReasons = new ConcurrentQueue<string>();
    var forgedSuccesses = 0;
    Parallel.For(0, 32, _ =>
    {
        try
        {
            forgedReader.ValidateRunAuthorization(initialRun);
            Interlocked.Increment(ref forgedSuccesses);
        }
        catch (StatisticReconciliationActualObservationException exception)
        {
            forgedReasons.Enqueue(exception.Reason);
        }
    });
    Require(forgedSuccesses == 0 &&
            forgedReasons.Count == 32 &&
            forgedReasons.All(reason =>
                reason ==
                "ACTUAL_MONGO_BOUNDARY_EXPECTED_AUTHORIZATION_MISMATCH"),
        "FORGED_EXPECTED_AUTH_CONCURRENT_FAIL_CLOSED");

    var tampered = AuthorizationRun(
        "STAT_RUN_READ",
        "WORK_VIEW");
    tampered.AuthorizationSnapshotHash = Hash("tampered-persisted-auth");
    ExpectMongoReason(
        () => AuthorizationReader(tampered.AuthorizationSnapshotHash)
            .ValidateRunAuthorization(tampered),
        "ACTUAL_MONGO_BOUNDARY_RUN_AUTHORIZATION_TAMPERED");

    var guard = AuthorizationReader(initialRun.AuthorizationSnapshotHash);
    var initialSha256 = guard.ValidateRunAuthorization(initialRun);
    var changedRun = AuthorizationRun(
        "STAT_RUN_EXPORT",
        "STAT_RUN_READ",
        "WORK_VIEW");
    var changedSha256 = guard.ValidateRunAuthorization(changedRun);
    Require(initialSha256 != changedSha256,
        "PERSISTED_AUTH_DRIFT_EXPOSED_TO_COORDINATOR");

    var before = Boundary(authorizationSha256: initialSha256);
    var after = Boundary(authorizationSha256: changedSha256);
    var result = await new StatisticReconciliationActualCoherentCaptureCoordinator()
        .CaptureAsync(
            before.ReconciliationId,
            new BoundaryReader(before, after),
            Steps(before, new List<string>()));
    Require(result.CaptureState ==
            StatisticReconciliationActualCoherentCaptureStates.Stale &&
            result.StaleReason == "BOUNDARY_DRIFT" &&
            result.CommonBoundary.AuthorizationSnapshotSha256 == initialSha256 &&
            result.FinalBoundary.AuthorizationSnapshotSha256 == changedSha256,
        "PRE_POST_AUTH_DRIFT_IS_STALE");
}

static async Task BoundaryPreQueryControlsAsync()
{
    var valid = new StatisticReconciliationActualMongoBoundarySlice(
        StatisticReconciliationActualBoundaryDomains.Source,
        "work_assignment_report",
        "scope-01",
        "p9-generation-17",
        1,
        new BsonDocument("workId", "work-01"),
        ["workId"]);
    var tooMany = Enumerable.Repeat(
            valid,
            StatisticReconciliationActualMongoBoundaryReader.MaxSlices + 1)
        .ToImmutableArray();
    await ExpectReasonAsync(
        () => new StatisticReconciliationActualMongoBoundaryReader(
                null!,
                BoundaryDescriptor(Hash("authorization"), tooMany))
            .ReadAsync(CancellationToken.None),
        "ACTUAL_MONGO_BOUNDARY_SLICE_COUNT_LIMIT");

    var forbidden = valid with
    {
        Filter = new BsonDocument("$where", "true")
    };
    await ExpectReasonAsync(
        () => new StatisticReconciliationActualMongoBoundaryReader(
                null!,
                BoundaryDescriptor(Hash("authorization"), [forbidden]))
            .ReadAsync(CancellationToken.None),
        "ACTUAL_MONGO_BOUNDARY_FILTER_OPERATOR_FORBIDDEN");

    var unknown = valid with
    {
        Collection = "users"
    };
    await ExpectReasonAsync(
        () => new StatisticReconciliationActualMongoBoundaryReader(
                null!,
                BoundaryDescriptor(Hash("authorization"), [unknown]))
            .ReadAsync(CancellationToken.None),
        "ACTUAL_MONGO_BOUNDARY_SLICE_COLLECTION_NOT_ALLOWED");
}

static StatisticReconciliationActualMongoBoundaryReader AuthorizationReader(
    string expectedAuthorizationSha256)
    => new(
        null!,
        BoundaryDescriptor(expectedAuthorizationSha256, []));

static StatisticReconciliationActualMongoBoundaryDescriptor BoundaryDescriptor(
    string expectedAuthorizationSha256,
    ImmutableArray<StatisticReconciliationActualMongoBoundarySlice> slices)
    => new(
        "reconciliation-01",
        "work-01",
        "scope-01",
        Hash("source-set"),
        Hash("config-bundle"),
        Hash("filter"),
        expectedAuthorizationSha256,
        slices);

static StatisticReconciliationRun AuthorizationRun(
    params string[] permissionCodes)
{
    var run = new StatisticReconciliationRun
    {
        ActorUserId = "actor-01",
        TenantUnitId = "unit-01",
        WorkId = "work-01",
        ScopeAssignmentId = "scope-01",
        PermissionCodes = permissionCodes
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList()
    };
    run.AuthorizationSnapshotHash =
        StatisticReconciliationActualMongoBoundaryReader
            .RunAuthorizationSha256(run);
    return run;
}

static void ExpectMongoReason(Action action, string expected)
{
    try
    {
        action();
    }
    catch (StatisticReconciliationActualObservationException exception)
    {
        Require(
            string.Equals(exception.Reason, expected, StringComparison.Ordinal),
            $"EXPECTED_{expected}_ACTUAL_{exception.Reason}");
        return;
    }
    throw new InvalidOperationException($"EXPECTED_{expected}");
}

static StatisticReconciliationActualCoherentBoundary Boundary(
    long resultRevision = 17,
    string? resultSemantic = null,
    bool reversePhysicalPins = false,
    string? authorizationSha256 = null)
{
    var pins = new List<StatisticReconciliationActualBoundaryPin>
    {
        Pin(StatisticReconciliationActualBoundaryDomains.Source,
            "report-membership", "p9-generation-17", 41, Hash("source")),
        Pin(StatisticReconciliationActualBoundaryDomains.Configuration,
            "p8-config-bundle", "p9-generation-17", 12, Hash("config")),
        Pin(StatisticReconciliationActualBoundaryDomains.Catalog,
            "catalog-v1.6", "p9-generation-17", 0, Hash("catalog")),
        Pin(StatisticReconciliationActualBoundaryDomains.Runtime,
            "flow-epoch", "p9-generation-17", 9, Hash("runtime")),
        Pin(StatisticReconciliationActualBoundaryDomains.Result,
            "p9-result", "p9-generation-17", resultRevision,
            resultSemantic ?? Hash("result")),
        Pin(StatisticReconciliationActualBoundaryDomains.Export,
            "p9-export", "p9-generation-17", 3, Hash("export"))
    };
    if (reversePhysicalPins)
        pins.Reverse();
    return StatisticReconciliationActualCoherentCaptureCoordinator.CreateBoundary(
        "reconciliation-01",
        "work-01",
        "scope-01",
        Hash("source-set"),
        Hash("config-bundle"),
        Hash("filter"),
        authorizationSha256 ?? Hash("authorization"),
        pins);
}

static StatisticReconciliationActualBoundaryPin Pin(
    string domain,
    string owner,
    string generation,
    long revision,
    string semantic)
    => new(domain, owner, generation, revision, semantic);

static ImmutableArray<StatisticReconciliationActualCoherentCaptureStep> Steps(
    StatisticReconciliationActualCoherentBoundary boundary,
    List<string> executionOrder,
    string? mixedLayer = null,
    string? mixedBoundarySha256 = null,
    string? staleLayer = null,
    bool emptyTyped = false)
    => StatisticReconciliationActualCoherentLayers.RequiredOrder
        .Select((layer, ordinal) =>
            new StatisticReconciliationActualCoherentCaptureStep(
                layer,
                (_, _) =>
                {
                    executionOrder.Add(layer);
                    var isStale = layer == staleLayer;
                    var ownerId = $"owner-{layer.ToLowerInvariant()}";
                    var ownerVersion = Hash($"owner-version-{layer}");
                    var typed = emptyTyped
                        ? []
                        : ImmutableArray.Create(
                            StatisticReconciliationActualTypedObservationCanonical.Create(
                            0,
                            layer,
                            ownerId,
                            ownerVersion,
                            "DIRECT",
                            "FIELD",
                            $"metric-{ordinal}",
                            "month:2026-08",
                            "SUM",
                            "NUMBER",
                            ordinal.ToString(),
                            numericValueCount: ordinal + 1,
                            fieldId: $"field-{ordinal}"));
                    return Task.FromResult(new StatisticReconciliationActualLayerCapture(
                        layer,
                        layer == mixedLayer
                            ? mixedBoundarySha256!
                            : boundary.BoundarySemanticSha256,
                        Hash($"capture-{ordinal}-{layer}"),
                        ordinal + 1,
                        isStale
                            ? StatisticReconciliationActualCoherentCaptureStates.Stale
                            : StatisticReconciliationActualCoherentCaptureStates.Ready,
                        isStale ? "API_GENERATION_DRIFT" : null,
                        ownerId,
                        ownerVersion,
                        typed));
                }))
        .ToImmutableArray();

static async Task ExpectReasonAsync(Func<Task> action, string reason)
{
    try
    {
        await action();
    }
    catch (StatisticReconciliationActualObservationException error)
    {
        Require(error.Reason == reason, $"EXPECTED_{reason}_GOT_{error.Reason}");
        return;
    }
    throw new InvalidOperationException($"EXPECTED_{reason}");
}

static string Hash(string value)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

static void Require(bool condition, string reason)
{
    if (!condition)
        throw new InvalidOperationException(reason);
}

internal sealed class BoundaryReader(
    StatisticReconciliationActualCoherentBoundary first,
    StatisticReconciliationActualCoherentBoundary second)
    : IStatisticReconciliationActualCoherentBoundaryReader
{
    internal int ReadCount { get; private set; }

    public Task<StatisticReconciliationActualCoherentBoundary> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        return Task.FromResult(ReadCount == 1 ? first : second);
    }
}
