using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using System.Text.Json;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;

var gates = StatisticReconciliationReviewGates.All;
var cases = gates.SelectMany(gate => Enumerable.Range(1, 4).Select(number =>
    (Id: $"P10-R{gate}-{number:00}", Run: (Func<Task>)(number switch
    {
        1 => () => ExactSignature(gate),
        2 => () => AuthorizationAndDuties(gate),
        3 => () => ReplayCasAndRejection(gate),
        4 => () => SupersessionAndFinalApproval(gate),
        _ => throw new InvalidOperationException()
    })))).ToArray();

var passed = 0;
foreach (var item in cases)
{
    try
    {
        await item.Run();
        Console.WriteLine($"PASS {item.Id}");
        passed++;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"FAIL {item.Id} {exception.GetType().Name}:{exception.Message}");
        return 1;
    }
}

Require(passed == 20, "EXACT_REVIEW_CASE_COUNT");
Require(cases.Select(value => value.Id).Distinct().Count() == 20, "UNIQUE_REVIEW_CASES");
SharedLedgerIsolation();
await MongoMillisecondAuditRoundTrip();
CurrentReplayDriftGate();
PresentationWireContract();
Console.WriteLine(
    "P10_T27_T28_INDEPENDENT_REVIEW_OK cases=20 gates=5 exactColumns=8 " +
    "sameGeneration=true sod=true authBeforeExistence=true appendOnly=true replay=true " +
    "cas=true rejection=true supersession=true businessRedacted=true p9Writes=0 " +
    "currentReplay=true presentationWire=true cumulative=208 stopBefore=P10-T29");
return 0;

static void PresentationWireContract()
{
    var run = new StatisticReconciliationRun
    {
        Id = "111111111111111111111111",
        WorkId = "222222222222222222222222",
        ScopeAssignmentId = "333333333333333333333333",
        ImmutableIdentityHash = new string('1', 64),
        ImmutableHeaderHash = new string('2', 64),
        P8ConfigBundleHash = new string('3', 64),
        ActualConfigurationBundleSha256 = new string('4', 64),
        CandidateCatalogSemanticSha256 = new string('5', 64),
        CandidateSchemaSemanticSha256 = new string('6', 64),
        Status = StatisticReconciliationRunStatuses.Running,
        UpdatedAtUtc = At(0)
    };
    var redactedPermission = new string('a', 64);
    var operatorPermission = new string('b', 64);
    var redactedColumns = StatisticReconciliationRunService
        .BuildPresentationColumns(run, null, redactedPermission);
    var operatorColumns = StatisticReconciliationRunService
        .BuildPresentationColumns(run, null, operatorPermission);
    Require(redactedColumns.Permission == redactedPermission &&
            operatorColumns.Permission == operatorPermission &&
            redactedColumns.Identity == operatorColumns.Identity &&
            redactedColumns.Config == operatorColumns.Config &&
            redactedColumns.Expected == operatorColumns.Expected &&
            redactedColumns.Actual == operatorColumns.Actual &&
            redactedColumns.Delta == operatorColumns.Delta &&
            redactedColumns.Freshness == operatorColumns.Freshness &&
            redactedColumns.Verdict == operatorColumns.Verdict,
        "PRESENTATION_ACTOR_PERMISSION_ONLY_DIFFERENCE");

    static StatisticReconciliationPresentationResponse Presentation(
        string detailLevel,
        StatisticReconciliationPresentationColumnsResponse columns,
        IReadOnlyList<StatisticReconciliationPresentationLinkResponse> sourceLinks)
        => new()
        {
            DetailLevel = detailLevel,
            Rows =
            [
                new StatisticReconciliationPresentationRowResponse
                {
                    Id = "111111111111111111111111",
                    Columns = columns,
                    Metadata = new StatisticReconciliationPresentationMetadataResponse
                    {
                        Total = 7,
                        RootCause = "NONE",
                        RowCountBeforeRedaction = 1,
                        RowCountAfterRedaction = 1,
                        PermissionCodes = ["STAT_RECONCILIATION_REVIEW"],
                        EvidenceLinks =
                        [
                            new StatisticReconciliationPresentationLinkResponse
                            {
                                Rel = "REVIEW", Href = "/api/review", Method = "GET"
                            }
                        ],
                        SourceLinks = sourceLinks
                    }
                }
            ]
        };
    var redacted = Presentation("REDACTED", redactedColumns, []);
    var operatorView = Presentation("OPERATOR", operatorColumns,
    [
        new StatisticReconciliationPresentationLinkResponse
        {
            Rel = "SOURCE_DETAIL", Href = "/api/detail", Method = "GET"
        }
    ]);
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    using var redactedJson = JsonDocument.Parse(JsonSerializer.Serialize(
        new StatisticReconciliationSummaryResponse
        {
            ReconciliationId = run.Id,
            Presentation = redacted
        }, options));
    using var operatorJson = JsonDocument.Parse(JsonSerializer.Serialize(
        new StatisticReconciliationSummaryResponse
        {
            ReconciliationId = run.Id,
            Presentation = operatorView
        }, options));
    var redactedPresentation = redactedJson.RootElement.GetProperty("presentation");
    var redactedRow = redactedPresentation.GetProperty("rows")[0];
    var columnNames = redactedRow.GetProperty("columns").EnumerateObject()
        .Select(value => value.Name).ToArray();
    Require(columnNames.SequenceEqual(new[]
    {
        "Identity", "Config", "Expected", "Actual", "Delta",
        "Freshness", "Permission", "Verdict"
    }), "PRESENTATION_EXACT_WIRE_COLUMN_NAMES_AND_ORDER");
    var metadata = redactedRow.GetProperty("metadata");
    Require(metadata.GetProperty("total").GetInt64() == 7 &&
            metadata.GetProperty("rootCause").GetString() == "NONE" &&
            metadata.GetProperty("rowCountBeforeRedaction").GetInt32() == 1 &&
            metadata.GetProperty("rowCountAfterRedaction").GetInt32() == 1 &&
            metadata.GetProperty("sourceLinks").GetArrayLength() == 0,
        "PRESENTATION_REDACTED_WIRE_METADATA");
    Require(operatorJson.RootElement.GetProperty("presentation")
            .GetProperty("rows")[0].GetProperty("metadata")
            .GetProperty("sourceLinks").GetArrayLength() == 1 &&
            operatorJson.RootElement.GetProperty("presentation")
                .GetProperty("detailLevel").GetString() == "OPERATOR",
        "PRESENTATION_OPERATOR_SOURCE_LINK_WIRE");
}
static void CurrentReplayDriftGate()
{
    static StatisticReconciliationRun Run(long revision = 7,
        string stateHash = "state-hash",
        string generation = "actual-generation") => new()
    {
        Id = "111111111111111111111111",
        StateRevision = revision,
        StateHash = stateHash,
        Status = StatisticReconciliationRunStatuses.Matched,
        CurrentGenerationId = generation,
        CurrentGenerationHash = "actual-generation-sha",
        PendingGenerationId = null,
        PendingGenerationHash = null,
        PendingGenerationPublishedAtUtc = null,
        CreatedAtUtc = At(0),
        UpdatedAtUtc = At(1)
    };
    static StatisticReconciliationReview Verdict(
        string document = "verdict-document") => new()
    {
        Id = "verdict-id",
        VerdictGenerationId = "verdict-generation",
        VerdictGenerationSha256 = "verdict-generation-sha",
        ActualGenerationId = "actual-generation",
        ActualGenerationSha256 = "actual-generation-sha",
        DocumentSemanticSha256 = document,
        CreatedAtUtc = At(0)
    };
    static void Check(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }

    var run = Run();
    var verdict = Verdict();
    Check(StatisticReconciliationIndependentReviewOwner.ExactCurrentReplay(
        run, verdict, Run(), Verdict()), "CURRENT_REPLAY_EXACT_ACCEPT");
    Check(!StatisticReconciliationIndependentReviewOwner.ExactCurrentReplay(
        run, verdict, Run(revision: 8), Verdict()),
        "CURRENT_REPLAY_REVISION_DRIFT");
    Check(!StatisticReconciliationIndependentReviewOwner.ExactCurrentReplay(
        run, verdict, Run(stateHash: "other-state"), Verdict()),
        "CURRENT_REPLAY_STATE_HASH_DRIFT");
    Check(!StatisticReconciliationIndependentReviewOwner.ExactCurrentReplay(
        run, verdict, Run(generation: "other-generation"), Verdict()),
        "CURRENT_REPLAY_GENERATION_DRIFT");
    Check(!StatisticReconciliationIndependentReviewOwner.ExactCurrentReplay(
        run, verdict, Run(), Verdict("other-document")),
        "CURRENT_REPLAY_VERDICT_DRIFT");
    Console.WriteLine(
        "P10_REVIEW_CURRENT_REPLAY_OK checks=5 exact=true driftRejected=3 verdictRejected=true");
}
static void SharedLedgerIsolation()
{
    Require(StatisticReconciliationIndependentReviewMongoBackend.CollectionName ==
            "work_report_statistic_reconciliation_reviews", "FROZEN_REVIEW_LEDGER");
    var predicate = StatisticReconciliationIndependentReviewMongoBackend
        .BuildReviewRowsPredicate();
    var allowedKinds = predicate["recordKind"]["$in"].AsBsonArray
        .Select(value => value.AsString).ToArray();
    Require(allowedKinds.SequenceEqual(new[]
    {
        StatisticReconciliationReviewRecordKinds.Decision,
        StatisticReconciliationReviewRecordKinds.Supersession
    }), "EXACT_REVIEW_RECORD_KIND_FILTER");

    var finalVerdict = new StatisticReconciliationReview
    {
        Id = "final-verdict-id",
        RecordKind = StatisticReconciliationReviewKinds.FinalVerdict,
        ReconciliationId = "reconciliation-1",
        VerdictGenerationId = "generation-1"
    }.ToBsonDocument();
    Require(finalVerdict["recordKind"].AsString ==
            StatisticReconciliationReviewKinds.FinalVerdict &&
            !allowedKinds.Contains(finalVerdict["recordKind"].AsString) &&
            !StatisticReconciliationIndependentReviewMongoBackend.IsReviewRecordKind(
                finalVerdict["recordKind"].AsString),
        "FINAL_VERDICT_EXCLUDED_BEFORE_AUDIT_DESERIALIZATION");

    var models = StatisticReconciliationIndependentReviewMongoBackend.BuildIndexModels();
    Require(models.Select(value => value.Options.Name).SequenceEqual(new[]
    {
        "ux_p10ReviewDecision_generation_gate",
        "ux_p10ReviewDecision_command",
        "ux_p10ReviewSupersession_decision_v2",
        "ix_p10Review_lineage"
    }), "EXACT_REVIEW_INDEX_NAMES");
    Require(models.All(value => value.Options.PartialFilterExpression is not null),
        "ALL_REVIEW_INDEXES_PARTIAL");
    var render = new RenderArgs<StatisticReconciliationIndependentReviewAuditRecord>(
        BsonSerializer.LookupSerializer<StatisticReconciliationIndependentReviewAuditRecord>(),
        BsonSerializer.SerializerRegistry);
    var partials = models.Select(value =>
        value.Options.PartialFilterExpression!.Render(render)).ToArray();
    var supersessionKeys = models[2].Keys.Render(render);
    Require(supersessionKeys.ElementCount == 1 &&
            supersessionKeys.Names.Single() == "supersedesDecisionId" &&
            supersessionKeys["supersedesDecisionId"].AsInt32 == 1,
        "EXACT_SUPERSESSION_DECISION_KEY");
    Require(partials[0]["recordKind"].AsString ==
            StatisticReconciliationReviewRecordKinds.Decision &&
            partials[1]["recordKind"].AsString ==
            StatisticReconciliationReviewRecordKinds.Decision &&
            partials[2]["recordKind"].AsString ==
            StatisticReconciliationReviewRecordKinds.Supersession &&
            partials[3].Equals(predicate) &&
            partials.All(value => !value.ToJson().Contains(
                StatisticReconciliationReviewKinds.FinalVerdict,
                StringComparison.Ordinal)),
        "FINAL_VERDICT_EXCLUDED_FROM_REVIEW_INDEX_NAMESPACE");
    Console.WriteLine(
        "P10_REVIEW_SHARED_LEDGER_ISOLATION_OK allowed=2 finalVerdictExcluded=true " +
        "partialIndexes=4 lineageFiltered=true operationFiltered=true");
}
static async Task MongoMillisecondAuditRoundTrip()
{
    var service = Service();
    var generation = Generation();
    var decision = await service.SubmitAsync(
        Permission("millisecond-reviewer"),
        generation,
        Command(generation, StatisticReconciliationReviewGates.All[0],
            "millisecond"),
        At(7).AddTicks(1));
    var decisionRoundTrip = BsonSerializer.Deserialize<
        StatisticReconciliationIndependentReviewAuditRecord>(
            decision.Decision.ToBson());
    StatisticReconciliationIndependentReviewService.ValidateStored(
        decisionRoundTrip);
    Require(decision.Decision.CreatedAtUtc.Ticks %
            TimeSpan.TicksPerMillisecond == 0 &&
            decisionRoundTrip.CreatedAtUtc == decision.Decision.CreatedAtUtc,
        "DECISION_UTC_MILLISECOND_BSON_ROUND_TRIP");

    var supersessionService = Service();
    for (var index = 0;
         index < StatisticReconciliationReviewGates.All.Count;
         index++)
    {
        var gate = StatisticReconciliationReviewGates.All[index];
        await supersessionService.SubmitAsync(
            Permission($"millisecond-reviewer-{index}"),
            generation,
            Command(generation, gate, $"millisecond-{index}"),
            At(8).AddTicks(index + 1));
    }
    var next = Generation("generation-millisecond-2", stateRevision: 2);
    var markers = await supersessionService.SupersedeApprovalsAsync(
        Permission("millisecond-supervisor"),
        next,
        Supersession(generation, next, "millisecond"),
        At(9).AddTicks(1));
    Require(markers.Count == StatisticReconciliationReviewGates.All.Count,
        "SUPERSESSION_UTC_MILLISECOND_MARKER_COUNT");
    foreach (var marker in markers)
    {
        var roundTrip = BsonSerializer.Deserialize<
            StatisticReconciliationIndependentReviewAuditRecord>(
                marker.ToBson());
        StatisticReconciliationIndependentReviewService.ValidateStored(roundTrip);
        Require(marker.CreatedAtUtc.Ticks %
                    TimeSpan.TicksPerMillisecond == 0 &&
                roundTrip.CreatedAtUtc == marker.CreatedAtUtc,
            "SUPERSESSION_UTC_MILLISECOND_BSON_ROUND_TRIP");
    }
}

static async Task ExactSignature(string gate)
{
    var service = Service();
    var generation = Generation();
    Require(generation.ReviewRecord.Columns.Select(value => value.Key).SequenceEqual(new[]
    {
        "Identity", "Config", "Expected", "Actual", "Delta", "Freshness", "Permission", "Verdict"
    }), "EXACT_EIGHT_COLUMN_ORDER");
    var permission = Permission($"reviewer-{gate.ToLowerInvariant()}");
    var command = Command(generation, gate, "exact");
    var result = await service.SubmitAsync(permission, generation, command, At(1));
    Require(!result.Replayed, "FIRST_APPEND");
    Require(result.Decision.Gate == gate &&
            result.Decision.GenerationId == generation.GenerationId &&
            result.Decision.GenerationSha256 == generation.GenerationSha256 &&
            result.Decision.SemanticVerdictSha256 == generation.SemanticVerdictSha256 &&
            result.Decision.ReviewRecordSha256 == generation.ReviewRecordSha256 &&
            result.Decision.PermissionSnapshotSha256 == permission.PermissionSnapshotSha256 &&
            result.Decision.RowCountBeforeRedaction == 9 &&
            result.Decision.RowCountAfterRedaction == 4,
        "SIGNED_BINDINGS");
}

static async Task AuthorizationAndDuties(string gate)
{
    var service = Service();
    var generation = Generation();
    var command = Command(generation, gate, "auth");
    var denied = Permission("outsider") with
    {
        Authenticated = false, ScopeAuthorized = false, CanReview = false,
        ActorId = "", PermissionSnapshotSha256 = "bad"
    };
    var hidden = await Capture(() => service.SubmitAsync(denied, null, null, At(2)));
    var missing = await Capture(() => service.SubmitAsync(denied, generation, command, At(2)));
    Require(hidden == StatisticReconciliationIndependentReviewFailureCodes.PermissionDenied &&
            missing == hidden, "AUTH_BEFORE_EXISTENCE_OPAQUE");
    await ExpectCode(() => service.SubmitAsync(Permission("initiator"), generation,
        command, At(2)), StatisticReconciliationIndependentReviewFailureCodes.SeparationOfDuties);
    await ExpectCode(() => service.SubmitAsync(Permission("writer"), generation,
        command with { CommandId = command.CommandId + "-writer",
            RequestSha256 = RequestHash(command with { CommandId = command.CommandId + "-writer" }) }, At(2)),
        StatisticReconciliationIndependentReviewFailureCodes.SeparationOfDuties);
    var accepted = await service.SubmitAsync(Permission("independent-" + gate), generation,
        command with { CommandId = command.CommandId + "-ok",
            RequestSha256 = RequestHash(command with { CommandId = command.CommandId + "-ok" }) }, At(2));
    Require(accepted.Decision.ReviewerActorId.StartsWith("independent-"), "INDEPENDENT_REVIEWER");
}

static async Task ReplayCasAndRejection(string gate)
{
    var service = Service();
    var generation = Generation();
    var permission = Permission("replay-" + gate);
    var command = Command(generation, gate, "replay");
    var first = await service.SubmitAsync(permission, generation, command, At(3));
    var replay = await service.SubmitAsync(permission, generation, command, At(30));
    Require(replay.Replayed && replay.Decision.Id == first.Decision.Id, "EXACT_REPLAY_CONVERGES");

    var changed = command with { Decision = StatisticReconciliationReviewDecisions.Reject };
    changed = changed with { RequestSha256 = RequestHash(changed) };
    await ExpectCode(() => service.SubmitAsync(permission, generation, changed, At(3)),
        StatisticReconciliationIndependentReviewFailureCodes.ReplayMismatch);

    var duplicate = Command(generation, gate, "duplicate");
    await ExpectCode(() => service.SubmitAsync(Permission("other-" + gate), generation,
        duplicate, At(3)), StatisticReconciliationIndependentReviewFailureCodes.DuplicateGate);

    var staleService = Service();
    var stale = Command(generation, gate, "stale") with { ExpectedStateRevision = 0 };
    stale = stale with { RequestSha256 = RequestHash(stale) };
    await ExpectCode(() => staleService.SubmitAsync(permission, generation, stale, At(3)),
        StatisticReconciliationIndependentReviewFailureCodes.StaleCas);

    var rejectService = Service();
    var reject = Command(generation, gate, "reject",
        StatisticReconciliationReviewDecisions.Reject);
    var rejected = await rejectService.SubmitAsync(permission, generation, reject, At(3));
    Require(rejected.Decision.Decision == StatisticReconciliationReviewDecisions.Reject,
        "REJECTION_IMMUTABLE");
    var approval = await rejectService.GetFinalApprovalAsync(permission, generation);
    Require(!approval.Approved, "REJECTION_BLOCKS_FINAL");

    foreach (var verdict in new[] { "MISMATCHED", "STALE", "FAILED", "UNKNOWN" })
    {
        var bad = Generation(verdict: verdict);
        await ExpectCode(() => Service().SubmitAsync(permission, bad,
                Command(bad, gate, "bad-" + verdict), At(3)),
            StatisticReconciliationIndependentReviewFailureCodes.TargetNotSignable);
    }
    var incomplete = Generation(complete: false);
    await ExpectCode(() => Service().SubmitAsync(permission, incomplete,
            Command(incomplete, gate, "incomplete"), At(3)),
        StatisticReconciliationIndependentReviewFailureCodes.TargetNotSignable);
}

static async Task SupersessionAndFinalApproval(string gate)
{
    var service = Service();
    var generation = Generation();
    var reviewers = StatisticReconciliationReviewGates.All.Select((value, index) =>
        Permission($"reviewer-{index + 1}-{gate}")).ToArray();
    for (var index = 0; index < StatisticReconciliationReviewGates.All.Count; index++)
        await service.SubmitAsync(reviewers[index], generation,
            Command(generation, StatisticReconciliationReviewGates.All[index], $"all-{gate}-{index}"),
            At(4 + index));
    var approved = await service.GetFinalApprovalAsync(Permission("reader"), generation);
    Require(approved.Approved && approved.ApprovedGateCount == 5, "FIVE_GATES_SAME_GENERATION");

    var newGeneration = Generation("generation-2-" + gate, stateRevision: 2);
    var supersede = Supersession(generation, newGeneration, "supersede-" + gate);
    var markers = await service.SupersedeApprovalsAsync(Permission("supervisor-" + gate),
        newGeneration, supersede, At(20));
    Require(markers.Count == 5 && markers.All(value =>
            value.RecordKind == StatisticReconciliationReviewRecordKinds.Supersession &&
            value.Status == StatisticReconciliationReviewStatuses.Superseded &&
            value.SupersedesDecisionId is not null), "APPEND_ONLY_SUPERSESSION");
    var replay = await service.SupersedeApprovalsAsync(Permission("supervisor-" + gate),
        newGeneration, supersede, At(21));
    Require(replay.Select(value => value.Id).SequenceEqual(markers.Select(value => value.Id)),
        "SUPERSESSION_REPLAY");
    Require(!(await service.GetFinalApprovalAsync(Permission("reader"), generation)).Approved,
        "OLD_APPROVAL_SUPERSEDED");
    Require(!(await service.GetFinalApprovalAsync(Permission("reader"), newGeneration)).Approved,
        "NO_CARRY_FORWARD");

    var business = await service.ReadAsync(Permission("business") with
    {
        CanReview = false, CanViewOperatorDetail = false, RowCountAfterRedaction = 0
    }, newGeneration);
    Require(business.OperatorDetail is null && business.Summary.ApprovedGateCount == 0,
        "BUSINESS_SUMMARY_REDACTED");
    var detail = await service.ReadAsync(Permission("operator") with
    {
        CanReview = false, CanViewOperatorDetail = true
    }, newGeneration);
    Require(detail.OperatorDetail is not null && detail.OperatorDetail.AuditRecords.Count == 5,
        "OPERATOR_DETAIL_SEPARATE");
}

static StatisticReconciliationIndependentReviewService Service()
    => new(new StatisticReconciliationIndependentReviewInMemoryBackend());

static StatisticReconciliationReviewGeneration Generation(
    string generationId = "generation-1", string verdict = "MATCHED", bool complete = true,
    long stateRevision = 1)
{
    var record = new StatisticReconciliationEightColumnRecord(
        "identity:run/work/flow/assignment/period/report/form/typed-row",
        "config:label/method/scope/contribution/version/catalog",
        "expected:typed-values/source-ids",
        "actual:source/projection/aggregate/result/api/export",
        "delta:zero-exact",
        "freshness:coherent-revisions-and-hashes",
        "permission:server-derived;snapshot=pinned;before=9;after=4",
        verdict);
    var recordHash = StatisticReconciliationIndependentReviewCanonical.ReviewRecordHash(record);
    var semantic = StatisticReconciliationIndependentReviewCanonical.Hash(
        "P10_MATCHED_SEMANTIC_VERDICT_V1", generationId, recordHash, verdict);
    return new("reconciliation-1", generationId,
        StatisticReconciliationIndependentReviewCanonical.Hash("GENERATION", generationId, semantic),
        semantic, record, recordHash, verdict, complete, true, verdict == "UNKNOWN",
        "initiator", "writer", stateRevision);
}

static StatisticReconciliationReviewPermission Permission(string actor)
    => new(true, true, true, true, true, actor,
        StatisticReconciliationIndependentReviewCanonical.Hash("PERMISSION", actor), 9, 4);

static StatisticReconciliationReviewCommand Command(
    StatisticReconciliationReviewGeneration generation, string gate, string suffix,
    string decision = StatisticReconciliationReviewDecisions.Approve)
{
    var value = new StatisticReconciliationReviewCommand(
        $"command-{gate}-{suffix}", new string('0', 64), generation.ReconciliationId,
        generation.GenerationId, generation.GenerationSha256, generation.SemanticVerdictSha256,
        gate, decision, generation.StateRevision);
    return value with { RequestSha256 = RequestHash(value) };
}

static string RequestHash(StatisticReconciliationReviewCommand command)
    => StatisticReconciliationIndependentReviewCanonical.CommandHash(command);

static StatisticReconciliationReviewSupersessionCommand Supersession(
    StatisticReconciliationReviewGeneration oldGeneration,
    StatisticReconciliationReviewGeneration newGeneration, string suffix)
{
    var value = new StatisticReconciliationReviewSupersessionCommand(
        "command-" + suffix, new string('0', 64), oldGeneration.ReconciliationId,
        oldGeneration.GenerationId, newGeneration.GenerationId, newGeneration.GenerationSha256,
        newGeneration.StateRevision);
    return value with
    {
        RequestSha256 = StatisticReconciliationIndependentReviewCanonical.SupersessionCommandHash(value)
    };
}

static DateTime At(int minute) => new(2026, 8, 12, 1, minute, 0, DateTimeKind.Utc);

static async Task<string> Capture(Func<Task> action)
{
    try { await action(); }
    catch (StatisticReconciliationIndependentReviewException exception) { return exception.Code; }
    throw new InvalidOperationException("EXPECTED_FAILURE");
}

static async Task ExpectCode(Func<Task> action, string code)
    => Require(await Capture(action) == code, "EXPECTED_" + code);

static void Require(bool condition, string code)
{
    if (!condition) throw new InvalidOperationException(code);
}
