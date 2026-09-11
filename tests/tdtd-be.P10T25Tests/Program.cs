using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

var cases = new (string Id, Func<Task> Run)[]
{
    ("P10-T25-01", AppendTypedContentCommitThenOneCasAsync),
    ("P10-T25-02", ReplayConflictCanonicalAndBoundsAsync),
    ("P10-T25-03", PartialAndCrashRecoveryAsync),
    ("P10-T25-04", StaleMissingExtraAndTamperForbiddenAsync)
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
        Console.WriteLine($"FAIL {item.Id} {error.GetType().Name}:{error.Message}");
        return 1;
    }
}

Require(passed == 4, "EXACT_CASE_COUNT");
Console.WriteLine(
    "P10_T25_ACTUAL_PUBLICATION_OK cases=4 layers=8 typedAtoms=9 " +
    "documents=18 lineagePins=17 typedRoundtrip=true partialInvisible=true " +
    "oneCas=true tamperRejected=true bounds=true stopBefore=P10-T26");
return 0;

static async Task AppendTypedContentCommitThenOneCasAsync()
{
    var fixture = await Fixture.CreateAsync();
    var events = new List<string>();
    var backend = new MemoryBackend(events);
    var cas = new IdempotentCas(events);
    var publisher = new StatisticReconciliationActualGenerationPublisher(
        backend,
        cas);
    var expectedAtoms = fixture.Generation.OrderedLayerObservations
        .SelectMany(item => item.TypedObservations)
        .ToImmutableArray();

    var result = await publisher.PublishCoherentAsync(
        fixture.Generation,
        fixture.Context,
        Utc(5),
        "worker-01",
        "claim-01",
        Actor());

    Require(!result.ExactReplay && result.PublishedByCas,
        "FIRST_APPEND_AND_CAS");
    Require(expectedAtoms.Length == 9 && result.TypedAtomCount == 9,
        "NONZERO_EXACT_TYPED_ATOM_COUNT");
    Require(result.DocumentCount == 18,
        "EIGHT_LAYERS_NINE_ATOMS_PLUS_COMMIT");
    Require(result.TypedObservations.SequenceEqual(expectedAtoms),
        "PUBLISH_RESULT_TYPED_ROUNDTRIP");
    Require(backend.ContentWriteCount == 1 && backend.CommitWriteCount == 1,
        "ONE_CONTENT_ONE_COMMIT_APPEND");
    Require(cas.EnsureCount == 1 && cas.PhysicalCasCount == 1,
        "EXACTLY_ONE_PHYSICAL_CAS");
    Require(events.IndexOf("APPEND_CONTENT") < events.IndexOf("APPEND_COMMIT") &&
            events.IndexOf("APPEND_COMMIT") < events.IndexOf("CAS_WRITE"),
        "APPEND_CONTENT_THEN_COMMIT_THEN_CAS");

    var layers = backend.Documents.Where(item =>
            item.RecordKind ==
            StatisticReconciliationActualPublicationRecordKinds.Layer)
        .OrderBy(item => item.ActualLayer?.Ordinal)
        .ToArray();
    var atoms = backend.Documents.Where(item =>
            item.RecordKind ==
            StatisticReconciliationActualPublicationRecordKinds.Atom)
        .ToArray();
    var commitDocument = backend.Documents.Single(item =>
        item.RecordKind ==
        StatisticReconciliationActualPublicationRecordKinds.Commit);
    var commit = commitDocument.Commit ??
                 throw new InvalidOperationException("COMMIT_REQUIRED");
    Require(layers.Length == 8 && atoms.Length == 9,
        "EXACT_LAYER_AND_ATOM_DOCUMENT_COUNTS");
    Require(backend.Documents.All(item =>
            item.SchemaVersion == "P10_ACTUAL_OBSERVATION_V7" &&
            item.AlgorithmRevision == "P10_ACTUAL_CAPTURE_V7"),
        "V7_SCHEMA_AND_ALGORITHM_ONLY");
    Require(backend.Documents.All(item =>
            item.P8ConfigurationBundleSha256 ==
                fixture.Context.P8ConfigurationBundleSha256 &&
            item.ActualConfigurationBundleSha256 ==
                fixture.Context.ActualConfigurationBundleSha256 &&
            item.P8ConfigurationBundleSha256 !=
                item.ActualConfigurationBundleSha256),
        "P8_AND_ACTUAL_CONFIGURATION_BUNDLES_DISTINCT_AND_PERSISTED");
    Require(atoms.All(item => item.LineagePin is not null && item.Atom is not null),
        "EVERY_ATOM_HAS_LINEAGE_AND_VALUE");
    Require(commit.LineagePinCount == 17 && commit.AtomCount == 9 &&
            commit.DocumentCount == 18,
        "COMMIT_BINDS_EXACT_LINEAGE_ATOM_DOCUMENT_COUNTS");
    Require(layers.Select(item => item.LineagePin?.Layer)
        .SequenceEqual(
            StatisticReconciliationActualCoherentLayers.RequiredOrder,
            StringComparer.Ordinal),
        "PERSISTED_REQUIRED_LAYER_ORDER");

    foreach (var source in fixture.Generation.OrderedLayerObservations)
    {
        var persisted = layers.Single(item =>
            item.ActualLayer?.Ordinal == source.Ordinal);
        var layerPayload = persisted.ActualLayer ??
                           throw new InvalidOperationException(
                               "ACTUAL_LAYER_REQUIRED");
        Require(layerPayload.ObservationCount == source.ObservationCount,
            $"RAW_COUNT_ROUNDTRIP_{source.Layer}");
        Require(layerPayload.TypedObservationCount ==
                source.TypedObservations.Length,
            $"TYPED_COUNT_ROUNDTRIP_{source.Layer}");
        Require(layerPayload.TypedObservationManifestSha256 ==
                source.TypedObservationManifestSha256,
            $"TYPED_MANIFEST_ROUNDTRIP_{source.Layer}");
    }

    var duplicateValueRows = result.TypedObservations.Where(item =>
            item.Layer ==
            StatisticReconciliationActualCoherentLayers.DirectProjection &&
            item.MetricId == "metric-direct")
        .ToArray();
    Require(duplicateValueRows.Length == 2 &&
            duplicateValueRows[0].ValueIdentitySha256 ==
            duplicateValueRows[1].ValueIdentitySha256 &&
            duplicateValueRows[0].OwnerId == duplicateValueRows[1].OwnerId &&
            duplicateValueRows[0].OwnerVersionSha256 ==
            duplicateValueRows[1].OwnerVersionSha256 &&
            duplicateValueRows[0].ProvenanceSha256 !=
            duplicateValueRows[1].ProvenanceSha256,
        "SAME_VALUE_DISTINCT_ORDINAL_MULTIPLICITY_PRESERVED");
    Require(result.TypedObservations.Any(item =>
            item.AtomKind == "STRING_LIST_UNORDERED" &&
            item.CanonicalValue == "[\"a\",\"b\"]"),
        "STRING_LIST_SEMANTICS_ROUNDTRIP");

    var visible = await publisher.ReadCompleteAsync(
        fixture.Generation.ReconciliationId,
        result.GenerationId) ??
        throw new InvalidOperationException("COMMITTED_GENERATION_VISIBLE");
    Require(visible.GenerationSemanticSha256 ==
            result.GenerationSemanticSha256 &&
            visible.ManifestSha256 == result.ManifestSha256,
        "READER_RETURNS_EXACT_GENERATION_AND_MANIFEST");
    Require(visible.TypedObservations.SequenceEqual(expectedAtoms),
        "READER_REHYDRATES_TYPED_ATOMS");
    Require(result.CommittedRunBinding.ActualConfigurationBundleSha256 ==
                fixture.Context.ActualConfigurationBundleSha256 &&
            visible.CommittedRunBinding.ActualConfigurationBundleSha256 ==
                fixture.Context.ActualConfigurationBundleSha256,
        "COMMIT_READBACK_RETURNS_ACTUAL_CONFIGURATION_BUNDLE");

    backend.Documents[0].ActualConfigurationBundleSha256 =
        Hash("tampered-actual-configuration-bundle");
    await ExpectAnyReasonAsync(
        () => publisher.ReadCompleteAsync(
            fixture.Generation.ReconciliationId,
            result.GenerationId),
        "ACTUAL_CONFIGURATION_BUNDLE_TAMPER_MUST_FAIL");
}

static async Task ReplayConflictCanonicalAndBoundsAsync()
{
    var fixture = await Fixture.CreateAsync();
    var events = new List<string>();
    var backend = new MemoryBackend(events);
    var cas = new IdempotentCas(events);
    var publisher = new StatisticReconciliationActualGenerationPublisher(
        backend,
        cas);
    await ExpectReasonAsync(
        () => publisher.PublishCoherentAsync(
            fixture.Generation,
            fixture.Context,
            Utc(6).AddTicks(1),
            "worker-02",
            "claim-02",
            Actor()),
        "ACTUAL_PUBLICATION_CREATED_AT_NOT_CANONICAL_UTC_MILLISECOND");
    Require(
        backend.Documents.Count == 0 &&
        backend.ContentWriteCount == 0 &&
        backend.CommitWriteCount == 0 &&
        cas.EnsureCount == 0,
        "NON_CANONICAL_CREATED_AT_FAILS_BEFORE_APPEND_OR_CAS");    await publisher.PublishCoherentAsync(
        fixture.Generation,
        fixture.Context,
        Utc(6),
        "worker-02",
        "claim-02",
        Actor());

    var replay = await publisher.PublishCoherentAsync(
        fixture.Generation,
        fixture.Context,
        Utc(7),
        "worker-02",
        "claim-02",
        Actor());
    Require(replay.ExactReplay && replay.PublishedByCas,
        "EXACT_REPLAY_ENSURES_PUBLICATION");
    Require(backend.ContentWriteCount == 1 && backend.CommitWriteCount == 1,
        "REPLAY_ZERO_STORE_WRITES");
    Require(cas.EnsureCount == 2 && cas.PhysicalCasCount == 1,
        "REPLAY_IDEMPOTENT_NO_SECOND_PHYSICAL_CAS");
    Require(backend.Documents.Select(item => item.CreatedAtUtc)
        .Distinct().Single() == Utc(6),
        "REPLAY_REUSES_PERSISTED_TIMESTAMP");

    await ExpectReasonAsync(
        () => publisher.PublishCoherentAsync(
            fixture.Generation,
            fixture.Context with { PeriodKey = "month:2026-09" },
            Utc(8),
            "worker-02",
            "claim-02",
            Actor()),
        "ACTUAL_PUBLICATION_IMMUTABLE_REPLAY_CONFLICT");
    Require(cas.EnsureCount == 2, "CONFLICT_NEVER_REACHES_CAS");

    ExpectReason(
        () => CreateSimpleAtom("SUM", "TEXT", "1"),
        "ACTUAL_TYPED_TYPED_ATOM_VALUE_TYPE_MISMATCH");
    ExpectReason(
        () => CreateSimpleAtom("SUM", "NUMBER", "01"),
        "ACTUAL_TYPED_TYPED_NUMBER_NON_CANONICAL");
    ExpectReason(
        () => CreateSimpleAtom("STRING_LIST", "STRING_LIST", "[\"a\"]"),
        "ACTUAL_TYPED_TYPED_ATOM_KIND_INVALID");
    ExpectReason(
        () => CreateSimpleAtom(
            "STRING_LIST_UNORDERED",
            "STRING_LIST",
            "[\"b\",\"a\"]"),
        "ACTUAL_TYPED_TYPED_CANONICAL_VALUE_INVALID");
    ExpectReason(
        () => CreateSimpleAtom(
            "TEXT",
            "TEXT",
            "x",
            occurrenceCount: 0),
        "ACTUAL_TYPED_TYPED_COUNTS_OR_SCALE_INVALID");
    ExpectReason(
        () => StatisticReconciliationActualTypedObservationCanonical.Create(
            0,
            StatisticReconciliationActualCoherentLayers.Api,
            "api-owner",
            Hash("api-owner-version"),
            "API",
            "FIELD",
            "metric-api-invalid-scope",
            "2026-08",
            "TEXT",
            "TEXT",
            "api-payload",
            fieldId: "field-api-invalid-scope",
            basicScope: "FLOW_FINAL",
            basicScopeId: "flow-final"),
        "ACTUAL_TYPED_TYPED_IDENTITY_SCOPE_MIXED");
    ExpectReason(
        () => StatisticReconciliationActualTypedObservationCanonical.Create(
            0,
            StatisticReconciliationActualCoherentLayers.Export,
            "export-owner",
            Hash("export-owner-version"),
            "EXPORT",
            "FIELD",
            "metric-export-invalid-channel",
            "2026-08",
            "ENUM",
            "ENUM",
            "OPEN",
            fieldId: "field-export-invalid-channel"),
        "ACTUAL_TYPED_TYPED_OPAQUE_CHANNEL_INVALID");

    var small = CreateSimpleAtom("TEXT", "TEXT", "x");
    var countBudget =
        new StatisticReconciliationActualTypedObservationCanonical
            .GenerationBudget();
    for (var index = 0;
         index < StatisticReconciliationActualTypedObservationCanonical
             .MaximumGenerationAtomCount;
         index++)
        countBudget.Observe(small);
    ExpectReason(
        () => countBudget.Observe(small),
        "ACTUAL_TYPED_TYPED_GENERATION_TOO_LARGE");

    var large = CreateSimpleAtom(
        "TEXT",
        "TEXT",
        new string('x', 65_536));
    var byteBudget =
        new StatisticReconciliationActualTypedObservationCanonical
            .GenerationBudget();
    var exactByteRows = checked((int)(
        StatisticReconciliationActualTypedObservationCanonical
            .MaximumGenerationCanonicalUtf8Bytes / 65_536));
    for (var index = 0; index < exactByteRows; index++)
        byteBudget.Observe(large);
    ExpectReason(
        () => byteBudget.Observe(large),
        "ACTUAL_TYPED_TYPED_GENERATION_BYTES_TOO_LARGE");
}

static async Task PartialAndCrashRecoveryAsync()
{
    var fixture = await Fixture.CreateAsync();
    var expectedContentCount =
        fixture.Context.ActualSourceDecisions.Length + 8 +
        fixture.Generation.OrderedLayerObservations
            .Sum(item => item.TypedObservations.Length);
    var partialEvents = new List<string>();
    var partialBackend = new MemoryBackend(partialEvents)
    {
        FailNextCommit = true
    };
    var partialCas = new IdempotentCas(partialEvents);
    var partialPublisher = new StatisticReconciliationActualGenerationPublisher(
        partialBackend,
        partialCas);
    await ExpectMessageAsync(
        () => partialPublisher.PublishCoherentAsync(
            fixture.Generation,
            fixture.Context,
            Utc(9),
            "worker-03",
            "claim-03",
            Actor()),
        "SIMULATED_COMMIT_FAILURE");
    Require(partialBackend.Documents.Count == expectedContentCount,
        "PARTIAL_CONTENT_DURABLE");
    Require(partialCas.EnsureCount == 0, "PARTIAL_NEVER_REACHES_CAS");
    var partialGenerationId = partialBackend.Documents
        .Select(item => item.GenerationId)
        .Distinct(StringComparer.Ordinal)
        .Single();
    Require(await partialPublisher.ReadCompleteAsync(
            fixture.Generation.ReconciliationId,
            partialGenerationId) is null,
        "PARTIAL_GENERATION_INVISIBLE");

    var recoveredPartial = await partialPublisher.PublishCoherentAsync(
        fixture.Generation,
        fixture.Context,
        Utc(10),
        "worker-03",
        "claim-03",
        Actor());
    Require(!recoveredPartial.ExactReplay,
        "PARTIAL_RETRY_FINISHES_COMMIT");
    Require(partialBackend.Documents.Count == expectedContentCount + 1,
        "PARTIAL_RETRY_COMPLETE");
    Require(partialBackend.Documents.Select(item => item.CreatedAtUtc)
        .Distinct().Single() == Utc(9),
        "PARTIAL_RETRY_REUSES_ORIGINAL_TIMESTAMP");
    Require(partialCas.PhysicalCasCount == 1,
        "PARTIAL_RETRY_ONE_PHYSICAL_CAS");

    var crashEvents = new List<string>();
    var crashBackend = new MemoryBackend(crashEvents);
    var crashCas = new IdempotentCas(crashEvents) { FailNextEnsure = true };
    var crashPublisher = new StatisticReconciliationActualGenerationPublisher(
        crashBackend,
        crashCas);
    await ExpectMessageAsync(
        () => crashPublisher.PublishCoherentAsync(
            fixture.Generation,
            fixture.Context,
            Utc(11),
            "worker-04",
            "claim-04",
            Actor()),
        "SIMULATED_CAS_LOSS");
    Require(crashBackend.Documents.Count == expectedContentCount + 1,
        "CRASH_AFTER_COMMIT_DURABLE");
    Require(!crashCas.IsPublished, "CAS_LOSS_NOT_PUBLISHED");
    var crashGenerationId = crashBackend.Documents
        .Select(item => item.GenerationId)
        .Distinct(StringComparer.Ordinal)
        .Single();
    Require(await crashPublisher.ReadCompleteAsync(
            fixture.Generation.ReconciliationId,
            crashGenerationId) is not null,
        "PRESEEDED_COMPLETE_COMMIT_VALID");

    var recoveredCrash = await crashPublisher.PublishCoherentAsync(
        fixture.Generation,
        fixture.Context,
        Utc(12),
        "worker-04",
        "claim-04",
        Actor());
    Require(recoveredCrash.ExactReplay, "CRASH_RETRY_STORE_REPLAY");
    Require(crashCas.EnsureCount == 2 && crashCas.PhysicalCasCount == 1 &&
            crashCas.IsPublished,
        "COMMIT_WITHOUT_CAS_RETRY_PERFORMS_REQUIRED_CAS");
}

static async Task StaleMissingExtraAndTamperForbiddenAsync()
{
    var stale = await Fixture.CreateAsync(stale: true);
    var staleBackend = new MemoryBackend(new List<string>());
    var staleCas = new IdempotentCas(new List<string>());
    var stalePublisher = new StatisticReconciliationActualGenerationPublisher(
        staleBackend,
        staleCas);
    await ExpectReasonAsync(
        () => stalePublisher.PublishCoherentAsync(
            stale.Generation,
            stale.Context,
            Utc(13),
            "worker-05",
            "claim-05",
            Actor()),
        "ACTUAL_PUBLICATION_COHERENT_GENERATION_NOT_READY");
    Require(staleBackend.Documents.Count == 0 && staleCas.EnsureCount == 0,
        "STALE_ZERO_WRITES_ZERO_CAS");

    var tampered = await PublishFreshAsync(Utc(14));
    SelfAttestAtomAndCommit(tampered.Backend);
    await ExpectReasonAsync(
        () => tampered.Publisher.ReadCompleteAsync(
            tampered.Fixture.Generation.ReconciliationId,
            tampered.Result.GenerationId),
        "ACTUAL_PUBLICATION_LAYER_TYPED_MANIFEST_MISMATCH");

    var missing = await PublishFreshAsync(Utc(15));
    missing.Backend.Documents.Remove(missing.Backend.Documents.First(item =>
        item.RecordKind ==
        StatisticReconciliationActualPublicationRecordKinds.Atom));
    await ExpectAnyReasonAsync(
        () => missing.Publisher.ReadCompleteAsync(
            missing.Fixture.Generation.ReconciliationId,
            missing.Result.GenerationId),
        "MISSING_ATOM_REJECTED");

    var extra = await PublishFreshAsync(Utc(16));
    extra.Backend.Documents.Add(extra.Backend.Documents.First(item =>
        item.RecordKind ==
        StatisticReconciliationActualPublicationRecordKinds.Atom));
    await ExpectAnyReasonAsync(
        () => extra.Publisher.ReadCompleteAsync(
            extra.Fixture.Generation.ReconciliationId,
            extra.Result.GenerationId),
        "EXTRA_ATOM_REJECTED");

    var timestamp = await PublishFreshAsync(Utc(17));
    timestamp.Backend.Documents[0].CreatedAtUtc =
        timestamp.Backend.Documents[0].CreatedAtUtc.AddMilliseconds(1);
    await ExpectReasonAsync(
        () => timestamp.Publisher.ReadCompleteAsync(
            timestamp.Fixture.Generation.ReconciliationId,
            timestamp.Result.GenerationId),
        "ACTUAL_PUBLICATION_ACTUAL_DOCUMENT_BINDING_MISMATCH");
}

static StatisticReconciliationActualTypedObservation CreateSimpleAtom(
    string atomKind,
    string valueType,
    string canonicalValue,
    long occurrenceCount = 1)
    => StatisticReconciliationActualTypedObservationCanonical.Create(
        0,
        StatisticReconciliationActualCoherentLayers.DirectProjection,
        "owner-simple",
        Hash("owner-simple-version"),
        "DIRECT",
        "FIELD",
        "metric-simple",
        "2026-08",
        atomKind,
        valueType,
        canonicalValue,
        occurrenceCount: occurrenceCount,
        fieldId: "field-simple");

static async Task<PublishedState> PublishFreshAsync(DateTime createdAtUtc)
{
    var fixture = await Fixture.CreateAsync();
    var backend = new MemoryBackend(new List<string>());
    var publisher = new StatisticReconciliationActualGenerationPublisher(
        backend,
        new IdempotentCas(new List<string>()));
    var result = await publisher.PublishCoherentAsync(
        fixture.Generation,
        fixture.Context,
        createdAtUtc,
        "worker-tamper",
        "claim-tamper",
        Actor());
    return new PublishedState(fixture, backend, publisher, result);
}

static void SelfAttestAtomAndCommit(MemoryBackend backend)
{
    var document = backend.Documents.First(item =>
        item.RecordKind ==
        StatisticReconciliationActualPublicationRecordKinds.Atom);
    var lineage = document.LineagePin ??
                  throw new InvalidOperationException("ATOM_LINEAGE_REQUIRED");
    var atom = document.Atom ??
               throw new InvalidOperationException("ATOM_VALUE_REQUIRED");
    var typed = StatisticReconciliationActualTypedObservationCanonical.Create(
        checked((int)lineage.Revision - 1),
        lineage.Layer,
        lineage.OwnerId,
        lineage.VersionId,
        atom.Family,
        atom.Kind,
        atom.MetricId,
        atom.PeriodKey ?? throw new InvalidOperationException("PERIOD_REQUIRED"),
        atom.AtomKind,
        atom.ValueType,
        "forged-value",
        atom.DecimalScale,
        atom.OccurrenceCount,
        atom.ReportCount,
        atom.RowCount,
        atom.NumericValueCount,
        atom.ValueState,
        atom.FieldId,
        atom.TableId,
        atom.RowId,
        atom.LabelId,
        atom.BasicScope,
        atom.BasicScopeId,
        atom.FlowBranchId,
        atom.FlowStepId,
        atom.AdvancedGrain,
        atom.DiffKind);
    atom.CanonicalValue = typed.CanonicalValue;
    atom.ValueIdentitySha256 = typed.ValueIdentitySha256;
    atom.AtomSemanticSha256 = typed.AtomSemanticSha256;
    lineage.Sha256 = typed.ProvenanceSha256;
    document.TypedSemanticSha256 = typed.AtomSemanticSha256;
    document.Id = StatisticReconciliationActualCanonical.Hash(
        "P10_ACTUAL_OBSERVATION_ID_V7",
        document.ReconciliationId,
        document.GenerationId,
        document.RecordKind,
        typed.Layer,
        StatisticReconciliationActualCanonical.Integer(typed.Ordinal),
        typed.IdentitySha256,
        typed.AtomKind,
        typed.ValueIdentitySha256);
    document.DocumentSemanticSha256 =
        StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_OBSERVATION_ATOM_DOCUMENT_V7",
            document.ContributionSemanticSha256,
            StatisticReconciliationActualCanonical.Integer(typed.Ordinal),
            typed.Layer,
            typed.OwnerId,
            typed.OwnerVersionSha256,
            typed.ProvenanceSha256,
            typed.IdentitySha256,
            typed.AtomKind,
            typed.ValueIdentitySha256,
            typed.AtomSemanticSha256);

    var commitDocument = backend.Documents.Single(item =>
        item.RecordKind ==
        StatisticReconciliationActualPublicationRecordKinds.Commit);
    var commit = commitDocument.Commit ??
                 throw new InvalidOperationException("COMMIT_REQUIRED");
    var manifest = StatisticReconciliationActualCanonical.HashSequence(
        "P10_ACTUAL_OBSERVATION_MANIFEST_V7",
        backend.Documents.Where(item =>
                item.RecordKind !=
                StatisticReconciliationActualPublicationRecordKinds.Commit)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.DocumentSemanticSha256));
    commit.ManifestSha256 = manifest;
    commitDocument.DocumentSemanticSha256 =
        StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_OBSERVATION_COMMIT_V7",
            commitDocument.ContributionSemanticSha256,
            manifest,
            StatisticReconciliationActualCanonical.Integer(
                commit.SourceDecisionCount),
            StatisticReconciliationActualCanonical.Integer(
                commit.LineagePinCount),
            StatisticReconciliationActualCanonical.Integer(commit.AtomCount),
            StatisticReconciliationActualCanonical.Integer(
                commit.DocumentCount));
}

static async Task ExpectReasonAsync(Func<Task> action, string reason)
{
    try
    {
        await action();
    }
    catch (StatisticReconciliationActualObservationException error)
    {
        Require(error.Reason == reason,
            $"EXPECTED_{reason}_GOT_{error.Reason}");
        return;
    }
    throw new InvalidOperationException($"EXPECTED_{reason}");
}

static async Task ExpectAnyReasonAsync(Func<Task> action, string marker)
{
    try
    {
        await action();
    }
    catch (StatisticReconciliationActualObservationException)
    {
        return;
    }
    throw new InvalidOperationException(marker);
}

static void ExpectReason(Action action, string reason)
{
    try
    {
        action();
    }
    catch (StatisticReconciliationActualObservationException error)
    {
        Require(error.Reason == reason,
            $"EXPECTED_{reason}_GOT_{error.Reason}");
        return;
    }
    throw new InvalidOperationException($"EXPECTED_{reason}");
}

static async Task ExpectMessageAsync(Func<Task> action, string message)
{
    try
    {
        await action();
    }
    catch (InvalidOperationException error)
    {
        Require(error.Message == message,
            $"EXPECTED_{message}_GOT_{error.Message}");
        return;
    }
    throw new InvalidOperationException($"EXPECTED_{message}");
}

static DateTime Utc(int hour)
    => new(2026, 8, 11, hour, 0, 0, DateTimeKind.Utc);

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

static string Hash(string value)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

static void Require(bool condition, string reason)
{
    if (!condition)
        throw new InvalidOperationException(reason);
}

internal sealed record PublishedState(
    Fixture Fixture,
    MemoryBackend Backend,
    StatisticReconciliationActualGenerationPublisher Publisher,
    StatisticReconciliationActualAppendResult Result);

internal sealed record Fixture(
    StatisticReconciliationActualCoherentGeneration Generation,
    StatisticReconciliationActualPublicationContext Context)
{
    internal static async Task<Fixture> CreateAsync(bool stale = false)
    {
        var before = Boundary();
        var after = stale
            ? Boundary(
                resultRevision: 18,
                resultSemantic: LocalHash("result-drift"))
            : before;
        var generation = await
            new StatisticReconciliationActualCoherentCaptureCoordinator()
                .CaptureAsync(
                    before.ReconciliationId,
                    new BoundaryReader(before, after),
                    Steps(before));
        var context = await PublicationV7Fixture.PrepareAsync(
            generation,
            BuildContext(before),
            CancellationToken.None);
        return new Fixture(generation, context);
    }

    private static StatisticReconciliationActualCoherentBoundary Boundary(
        long resultRevision = 17,
        string? resultSemantic = null)
    {
        var pins = new[]
        {
            Pin(StatisticReconciliationActualBoundaryDomains.Source,
                "source-owner", 4, LocalHash("source")),
            Pin(StatisticReconciliationActualBoundaryDomains.Configuration,
                "configuration-owner", 5, LocalHash("configuration")),
            Pin(StatisticReconciliationActualBoundaryDomains.Catalog,
                "catalog-owner", 6, LocalHash("catalog")),
            Pin(StatisticReconciliationActualBoundaryDomains.Runtime,
                "runtime-owner", 7, LocalHash("runtime")),
            Pin(StatisticReconciliationActualBoundaryDomains.Result,
                "result-owner", resultRevision,
                resultSemantic ?? LocalHash("result")),
            Pin(StatisticReconciliationActualBoundaryDomains.Export,
                "export-owner", 8, LocalHash("export"))
        };
        return StatisticReconciliationActualCoherentCaptureCoordinator
            .CreateBoundary(
                "reconciliation-01",
                "work-01",
                "scope-01",
                LocalHash("source-set"),
                LocalHash("configuration-bundle"),
                LocalHash("filter"),
                LocalHash("authorization"),
                pins);
    }

    private static StatisticReconciliationActualBoundaryPin Pin(
        string domain,
        string owner,
        long revision,
        string semantic)
        => new(domain, owner, "p9-generation-17", revision, semantic);

    private static ImmutableArray<StatisticReconciliationActualCoherentCaptureStep>
        Steps(StatisticReconciliationActualCoherentBoundary boundary)
        => StatisticReconciliationActualCoherentLayers.RequiredOrder
            .Select((layer, ordinal) =>
                new StatisticReconciliationActualCoherentCaptureStep(
                    layer,
                    (_, _) => Task.FromResult(
                        new StatisticReconciliationActualLayerCapture(
                            layer,
                            boundary.BoundarySemanticSha256,
                            LocalHash($"capture-{ordinal}-{layer}"),
                            ordinal + 10,
                            StatisticReconciliationActualCoherentCaptureStates.Ready,
                            null,
                            $"layer-owner-{ordinal}",
                            LocalHash($"layer-owner-version-{ordinal}"),
                            TypedRows(layer, ordinal)))))
            .ToImmutableArray();

    private static ImmutableArray<StatisticReconciliationActualTypedObservation>
        TypedRows(string layer, int layerOrdinal)
    {
        var owner = $"layer-owner-{layerOrdinal}";
        var ownerVersion = LocalHash($"layer-owner-version-{layerOrdinal}");
        return layer switch
        {
            StatisticReconciliationActualCoherentLayers.SourceMembership => [],
            StatisticReconciliationActualCoherentLayers.DirectProjection =>
            [
                Typed(
                    0, layer, owner, ownerVersion,
                    "DIRECT", "FIELD", "metric-direct", "TEXT", "TEXT",
                    "same-value", fieldId: "field-direct"),
                Typed(
                    1, layer, owner, ownerVersion,
                    "DIRECT", "FIELD", "metric-direct", "TEXT", "TEXT",
                    "same-value", fieldId: "field-direct"),
                Typed(
                    2, layer, owner, ownerVersion,
                    "DIRECT", "FIELD", "metric-list",
                    "STRING_LIST_UNORDERED", "STRING_LIST", "[\"a\",\"b\"]",
                    fieldId: "field-list")
            ],
            StatisticReconciliationActualCoherentLayers.Aggregate =>
            [
                Typed(
                    0, layer, owner, ownerVersion,
                    "DIRECT", "TABLE", "metric-aggregate", "SUM", "NUMBER",
                    "12.5", decimalScale: 1, tableId: "table-aggregate",
                    numericValueCount: 4)
            ],
            StatisticReconciliationActualCoherentLayers.Basic =>
            [
                Typed(
                    0, layer, owner, ownerVersion,
                    "BASIC", "FIELD", "metric-basic", "COUNT", "NUMBER",
                    "3", fieldId: "field-basic", basicScope: "FLOW_FINAL",
                    basicScopeId: "flow-final", reportCount: 3)
            ],
            StatisticReconciliationActualCoherentLayers.Advanced =>
            [
                Typed(
                    0, layer, owner, ownerVersion,
                    "ADVANCED", "FIELD", "metric-advanced", "FULL_DATE",
                    "FULL_DATE", "DAY:2026-08-11", fieldId: "field-advanced",
                    advancedGrain: "MONTH")
            ],
            StatisticReconciliationActualCoherentLayers.Diff =>
            [
                Typed(
                    0, layer, owner, ownerVersion,
                    "DIFF", "FIELD", "metric-diff", "BOOLEAN", "BOOLEAN",
                    "true", fieldId: "field-diff", diffKind: "FIELD")
            ],
            StatisticReconciliationActualCoherentLayers.Api =>
            [
                Typed(
                    0, layer, owner, ownerVersion,
                    "API", "FIELD", "metric-api", "TEXT", "TEXT",
                    "api-exact-payload", fieldId: "field-api")
            ],
            StatisticReconciliationActualCoherentLayers.Export =>
            [
                Typed(
                    0, layer, owner, ownerVersion,
                    "EXPORT", "FIELD", "metric-export", "TEXT", "TEXT",
                    "export-exact-payload", fieldId: "field-export")
            ],
            _ => throw new InvalidOperationException("UNEXPECTED_LAYER")
        };
    }

    private static StatisticReconciliationActualTypedObservation Typed(
        int ordinal,
        string layer,
        string ownerId,
        string ownerVersion,
        string family,
        string kind,
        string metricId,
        string atomKind,
        string valueType,
        string canonicalValue,
        int decimalScale = 0,
        long reportCount = 1,
        long numericValueCount = 0,
        string? fieldId = null,
        string? tableId = null,
        string? basicScope = null,
        string? basicScopeId = null,
        string? advancedGrain = null,
        string? diffKind = null)
        => StatisticReconciliationActualTypedObservationCanonical.Create(
            ordinal,
            layer,
            ownerId,
            ownerVersion,
            family,
            kind,
            metricId,
            "2026-08",
            atomKind,
            valueType,
            canonicalValue,
            decimalScale,
            reportCount: reportCount,
            numericValueCount: numericValueCount,
            fieldId: fieldId,
            tableId: tableId,
            basicScope: basicScope,
            basicScopeId: basicScopeId,
            advancedGrain: advancedGrain,
            diffKind: diffKind);

    private static StatisticReconciliationActualPublicationContext BuildContext(
        StatisticReconciliationActualCoherentBoundary boundary)
    {
        var catalog = CatalogPins();
        return new StatisticReconciliationActualPublicationContext(
            boundary.ReconciliationId,
            LocalHash("immutable-identity"),
            LocalHash("immutable-header"),
            boundary.WorkId,
            boundary.ScopeAssignmentId,
            "month:2026-08",
            "2026-08",
            "concept-01",
            "MONTH",
            "PERIOD_END",
            boundary.FilterSha256,
            "form-version-01",
            LocalHash("form-schema"),
            "flow-template-01",
            LocalHash("flow-payload"),
            "flow-instance-01",
            "epoch-01",
            3,
            9,
            "p8-owner-01",
            LocalHash("p8-direct-configuration-bundle"),
            boundary.ConfigurationBundleSha256,
            boundary.CatalogPinSetSha256,
            catalog,
            RuntimeKind: StatisticReconciliationExpectedRuntimeKinds.Flow);
    }

    private static StatisticReconciliationActualPublicationCatalogPins CatalogPins()
    {
        var values = new[]
        {
            "v1.6",
            LocalHash("p9-catalog-raw"),
            LocalHash("p9-catalog-semantic"),
            LocalHash("p9-schema-raw"),
            LocalHash("p9-schema-semantic"),
            LocalHash("p9-stage-lock"),
            "p10-chain-01",
            "P10-03",
            "v1.7-candidate",
            LocalHash("candidate-catalog-raw"),
            LocalHash("candidate-catalog-semantic"),
            LocalHash("candidate-schema-raw"),
            LocalHash("candidate-schema-semantic"),
            LocalHash("candidate-stage-lock")
        };
        var pinSet = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CATALOG_PIN_SET_V1",
            values);
        return new StatisticReconciliationActualPublicationCatalogPins(
            values[0], values[1], values[2], values[3], values[4], values[5],
            values[6], values[7], values[8], values[9], values[10], values[11],
            values[12], values[13], pinSet);
    }

    private static string LocalHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

internal sealed class BoundaryReader(
    StatisticReconciliationActualCoherentBoundary first,
    StatisticReconciliationActualCoherentBoundary second)
    : IStatisticReconciliationActualCoherentBoundaryReader
{
    private int _readCount;

    public Task<StatisticReconciliationActualCoherentBoundary> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _readCount++;
        return Task.FromResult(_readCount == 1 ? first : second);
    }
}

internal sealed class MemoryBackend(List<string> events)
    : IStatisticReconciliationActualObservationBackend
{
    internal List<StatisticReconciliationObservation> Documents { get; } = [];
    internal int ContentWriteCount { get; private set; }
    internal int CommitWriteCount { get; private set; }
    internal bool FailNextCommit { get; set; }

    public Task<IReadOnlyList<StatisticReconciliationObservation>>
        ReadGenerationAsync(
            string reconciliationId,
            string generationId,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        events.Add("READ");
        return Task.FromResult<IReadOnlyList<StatisticReconciliationObservation>>(
            Documents.Where(item =>
                    item.ReconciliationId == reconciliationId &&
                    item.GenerationId == generationId &&
                    item.SchemaVersion ==
                    StatisticReconciliationActualGenerationPublisher.SchemaVersion &&
                    item.RecordKind is
                    StatisticReconciliationObservationRecordKinds.ActualLayer or
                    StatisticReconciliationObservationRecordKinds.ActualAtom or
                    StatisticReconciliationObservationRecordKinds.ActualSourceDecision or
                    StatisticReconciliationObservationRecordKinds.GenerationCommit)
                .ToArray());
    }

    public Task AppendContentAsync(
        IReadOnlyList<StatisticReconciliationObservation> observations,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ContentWriteCount++;
        events.Add("APPEND_CONTENT");
        AppendUnique(observations);
        return Task.CompletedTask;
    }

    public Task AppendCommitAsync(
        StatisticReconciliationObservation observation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitWriteCount++;
        events.Add("APPEND_COMMIT");
        if (FailNextCommit)
        {
            FailNextCommit = false;
            throw new InvalidOperationException("SIMULATED_COMMIT_FAILURE");
        }
        AppendUnique([observation]);
        return Task.CompletedTask;
    }

    private void AppendUnique(
        IEnumerable<StatisticReconciliationObservation> observations)
    {
        foreach (var observation in observations)
        {
            if (Documents.All(item => item.Id != observation.Id))
                Documents.Add(observation);
        }
    }
}

internal sealed class IdempotentCas(List<string> events)
    : IStatisticReconciliationActualGenerationCas
{
    private string? _reconciliationId;
    private string? _generationId;
    private string? _generationHash;

    internal int EnsureCount { get; private set; }
    internal int PhysicalCasCount { get; private set; }
    internal bool FailNextEnsure { get; set; }
    internal bool IsPublished => _generationId is not null;

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
        EnsureCount++;
        events.Add("CAS_ENSURE");
        if (FailNextEnsure)
        {
            FailNextEnsure = false;
            throw new InvalidOperationException("SIMULATED_CAS_LOSS");
        }
        if (_generationId is null)
        {
            _reconciliationId = reconciliationId;
            _generationId = generationId;
            _generationHash = generationSemanticSha256;
            PhysicalCasCount++;
            events.Add("CAS_WRITE");
        }
        else if (_reconciliationId == reconciliationId &&
                 _generationId == generationId &&
                 _generationHash == generationSemanticSha256)
        {
            events.Add("CAS_REPLAY");
        }
        else
        {
            throw new InvalidOperationException("SIMULATED_CAS_CONFLICT");
        }
        return Task.CompletedTask;
    }
}
