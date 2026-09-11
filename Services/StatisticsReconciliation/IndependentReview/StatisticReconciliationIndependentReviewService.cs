namespace tdtd_be.Services.StatisticsReconciliation.IndependentReview;

public sealed class StatisticReconciliationIndependentReviewService(
    IStatisticReconciliationIndependentReviewBackend backend)
{
    private readonly IStatisticReconciliationIndependentReviewBackend _backend =
        backend ?? throw new ArgumentNullException(nameof(backend));

    public async Task<StatisticReconciliationReviewSubmissionResult> SubmitAsync(
        StatisticReconciliationReviewPermission permission,
        StatisticReconciliationReviewGeneration? generation,
        StatisticReconciliationReviewCommand? command,
        DateTime createdAtUtc,
        CancellationToken ct = default)
    {
        Authorize(permission, requireReviewer: true);
        ValidateGeneration(generation, requireSignable: true);
        ValidateCommand(command, generation!, requireStateRevision: false);

        if (permission.ActorId == generation!.RunInitiatorActorId ||
            permission.ActorId == generation.LatestGenerationWriterActorId)
            throw Error(StatisticReconciliationIndependentReviewFailureCodes.SeparationOfDuties,
                "reviewerMustDifferFromInitiatorAndWriter");

        var priorOperation = await _backend.ReadOperationAsync(command!.CommandId, ct);
        if (priorOperation.Count > 0)
        {
            var replay = priorOperation.SingleOrDefault();
            if (replay is null || replay.RequestSha256 != command.RequestSha256 ||
                replay.ReviewerActorId != permission.ActorId ||
                replay.PermissionSnapshotSha256 != permission.PermissionSnapshotSha256)
                throw Error(StatisticReconciliationIndependentReviewFailureCodes.ReplayMismatch,
                    "changedReplay");
            ValidateStored(replay);
            return new(true, replay);
        }

        ValidateCommand(command, generation,
            requireStateRevision: true);
        var lineage = await _backend.ReadLineageAsync(command.ReconciliationId, ct);
        var decisions = lineage.Where(value =>
            value.RecordKind == StatisticReconciliationReviewRecordKinds.Decision &&
            value.GenerationId == command.GenerationId).ToArray();
        if (decisions.Any(value => value.Gate == command.Gate))
            throw Error(StatisticReconciliationIndependentReviewFailureCodes.DuplicateGate,
                "oneDecisionPerGateAndGeneration");

        var record = BuildDecision(permission, generation, command, createdAtUtc);
        var outcome = await _backend.TryAppendDecisionAsync(record, ct);
        if (outcome == StatisticReconciliationReviewAppendOutcome.Appended)
            return new(false, record);

        priorOperation = await _backend.ReadOperationAsync(command.CommandId, ct);
        if (outcome == StatisticReconciliationReviewAppendOutcome.DuplicateId &&
            priorOperation.Count == 1 &&
            priorOperation[0].DocumentSha256 == record.DocumentSha256)
            return new(true, priorOperation[0]);
        if (outcome ==
            StatisticReconciliationReviewAppendOutcome.StateFenceLost)
            throw Error(
                StatisticReconciliationIndependentReviewFailureCodes.StaleCas,
                "currentGenerationChangedBeforeAppend");
        throw Error(
            outcome == StatisticReconciliationReviewAppendOutcome.DuplicateGate
                ? StatisticReconciliationIndependentReviewFailureCodes.DuplicateGate
                : StatisticReconciliationIndependentReviewFailureCodes.ReplayMismatch,
            "appendConflict");
    }

    public async Task<IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>>
        SupersedeApprovalsAsync(
            StatisticReconciliationReviewPermission permission,
            StatisticReconciliationReviewGeneration? newGeneration,
            StatisticReconciliationReviewSupersessionCommand? command,
            DateTime createdAtUtc,
            CancellationToken ct = default)
    {
        Authorize(permission, requireReviewer: true);
        ValidateGeneration(newGeneration, requireSignable: false);
        ValidateSupersessionCommand(command, newGeneration!,
            requireStateRevision: false);

        var existingOperation = await _backend.ReadOperationAsync(command!.CommandId, ct);
        if (existingOperation.Count > 0)
        {
            if (existingOperation.Any(value => value.RequestSha256 != command.RequestSha256 ||
                                               value.ReviewerActorId != permission.ActorId))
                throw Error(StatisticReconciliationIndependentReviewFailureCodes.ReplayMismatch,
                    "changedSupersessionReplay");
            existingOperation.ToList().ForEach(ValidateStored);
            return existingOperation;
        }

        ValidateSupersessionCommand(command, newGeneration,
            requireStateRevision: true);
        var lineage = await _backend.ReadLineageAsync(command.ReconciliationId, ct);
        var alreadySuperseded = lineage
            .Where(value => value.RecordKind == StatisticReconciliationReviewRecordKinds.Supersession)
            .Select(value => value.SupersedesDecisionId).Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        var approvals = lineage.Where(value =>
                value.RecordKind == StatisticReconciliationReviewRecordKinds.Decision &&
                value.GenerationId == command.PreviousGenerationId &&
                value.Decision == StatisticReconciliationReviewDecisions.Approve &&
                !alreadySuperseded.Contains(value.Id))
            .OrderBy(value => value.Gate).ToArray();
        var markers = approvals.Select(value => BuildSupersession(
            permission, newGeneration!, command, value, createdAtUtc)).ToArray();
        var outcome = await _backend.TryAppendSupersessionsAsync(
            markers, ct);
        if (outcome ==
            StatisticReconciliationReviewAppendOutcome.Appended)
            return markers;

        existingOperation = await _backend.ReadOperationAsync(
            command.CommandId, ct);
        if (outcome ==
                StatisticReconciliationReviewAppendOutcome.DuplicateId &&
            existingOperation.Count == markers.Length &&
            existingOperation.Zip(markers).All(pair =>
                pair.First.DocumentSha256 == pair.Second.DocumentSha256))
            return existingOperation;
        if (outcome ==
            StatisticReconciliationReviewAppendOutcome.StateFenceLost)
            throw Error(
                StatisticReconciliationIndependentReviewFailureCodes.StaleCas,
                "supersessionStateFenceLost");
        throw Error(
            StatisticReconciliationIndependentReviewFailureCodes.ReplayMismatch,
            "supersessionConflict");
    }

    public async Task<StatisticReconciliationReviewFinalApproval> GetFinalApprovalAsync(
        StatisticReconciliationReviewPermission permission,
        StatisticReconciliationReviewGeneration? generation,
        CancellationToken ct = default)
    {
        Authorize(permission, requireReviewer: false);
        ValidateGeneration(generation, requireSignable: false);
        var active = await ReadActiveDecisionsAsync(generation!, ct);
        var approvals = active.Where(value =>
            value.Decision == StatisticReconciliationReviewDecisions.Approve &&
            value.SemanticVerdictSha256 == generation!.SemanticVerdictSha256 &&
            value.GenerationSha256 == generation.GenerationSha256).ToArray();
        var approved = generation!.Verdict == "MATCHED" && generation.CompleteEvidence &&
                       generation.CoherentSnapshot && !generation.UnknownRootCause &&
                       approvals.Select(value => value.Gate).Distinct().Count() == 5 &&
                       StatisticReconciliationReviewGates.All.All(gate =>
                           approvals.Count(value => value.Gate == gate) == 1) &&
                       approvals.Select(value => value.ReviewerActorId).Distinct().Count() == 5;
        return new(generation.ReconciliationId, generation.GenerationId, approved,
            approvals.Length, StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_REVIEW_FINAL_APPROVAL_V1", generation.ReconciliationId,
                generation.GenerationId, generation.GenerationSha256,
                generation.SemanticVerdictSha256, approved,
                approvals.OrderBy(value => value.Gate).Select(value => value.DocumentSha256)
                    .Aggregate("", (left, right) => left + right)));
    }

    public async Task<StatisticReconciliationReviewReadResult> ReadAsync(
        StatisticReconciliationReviewPermission permission,
        StatisticReconciliationReviewGeneration? generation,
        CancellationToken ct = default)
    {
        Authorize(permission, requireReviewer: false);
        ValidateGeneration(generation, requireSignable: false);
        var lineage = await _backend.ReadLineageAsync(generation!.ReconciliationId, ct);
        var active = Effective(lineage, generation.GenerationId);
        var approval = await GetFinalApprovalAsync(permission, generation, ct);
        var gateStates = StatisticReconciliationReviewGates.All.ToDictionary(
            gate => gate,
            gate => active.SingleOrDefault(value => value.Gate == gate)?.Decision ?? "PENDING");
        var summary = new StatisticReconciliationReviewBusinessSummaryDto(
            generation.ReconciliationId, generation.GenerationId, approval.Approved,
            active.Count(value => value.Decision == StatisticReconciliationReviewDecisions.Approve),
            active.Count(value => value.Decision == StatisticReconciliationReviewDecisions.Reject),
            gateStates);
        var detail = permission.CanViewOperatorDetail
            ? new StatisticReconciliationReviewOperatorDetailDto(summary,
                lineage.Where(value => value.GenerationId == generation.GenerationId ||
                                       value.SupersededByGenerationId == generation.GenerationId)
                    .ToArray())
            : null;
        return new(summary, detail);
    }

    private async Task<IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>>
        ReadActiveDecisionsAsync(StatisticReconciliationReviewGeneration generation, CancellationToken ct)
        => Effective(await _backend.ReadLineageAsync(generation.ReconciliationId, ct),
            generation.GenerationId);

    public async Task<IReadOnlyList<string>> ReadSupersessionCandidatesAsync(
        string reconciliationId, string currentGenerationId,
        CancellationToken ct = default)
    {
        var lineage = await _backend.ReadLineageAsync(reconciliationId, ct);
        lineage.ToList().ForEach(ValidateStored);
        var superseded = lineage.Where(value =>
                value.RecordKind ==
                    StatisticReconciliationReviewRecordKinds.Supersession)
            .Select(value => value.SupersedesDecisionId)
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        return lineage.Where(value =>
                value.RecordKind ==
                    StatisticReconciliationReviewRecordKinds.Decision &&
                value.Decision ==
                    StatisticReconciliationReviewDecisions.Approve &&
                value.GenerationId != currentGenerationId &&
                !superseded.Contains(value.Id))
            .Select(value => value.GenerationId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord> Effective(
        IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord> lineage,
        string generationId)
    {
        lineage.ToList().ForEach(ValidateStored);
        var superseded = lineage.Where(value =>
                value.RecordKind == StatisticReconciliationReviewRecordKinds.Supersession)
            .Select(value => value.SupersedesDecisionId).Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        return lineage.Where(value =>
            value.RecordKind == StatisticReconciliationReviewRecordKinds.Decision &&
            value.GenerationId == generationId && !superseded.Contains(value.Id)).ToArray();
    }

    private static void Authorize(StatisticReconciliationReviewPermission permission,
        bool requireReviewer)
    {
        if (permission is null || !permission.ServerDerived || !permission.Authenticated ||
            !permission.ScopeAuthorized || requireReviewer && !permission.CanReview)
            throw Error(StatisticReconciliationIndependentReviewFailureCodes.PermissionDenied,
                "opaqueTarget");
        StatisticReconciliationIndependentReviewCanonical.RequireText(permission.ActorId, "actorId");
        StatisticReconciliationIndependentReviewCanonical.RequireSha(
            permission.PermissionSnapshotSha256, "permissionSnapshotSha256");
        if (permission.RowCountBeforeRedaction < 0 || permission.RowCountAfterRedaction < 0 ||
            permission.RowCountAfterRedaction > permission.RowCountBeforeRedaction)
            throw StatisticReconciliationIndependentReviewCanonical.Invalid("redactionCounts");
    }

    private static void ValidateGeneration(StatisticReconciliationReviewGeneration? value,
        bool requireSignable)
    {
        if (value is null)
            throw StatisticReconciliationIndependentReviewCanonical.Invalid("generation");
        StatisticReconciliationIndependentReviewCanonical.RequireText(value.ReconciliationId,
            "reconciliationId");
        StatisticReconciliationIndependentReviewCanonical.RequireText(value.GenerationId,
            "generationId");
        StatisticReconciliationIndependentReviewCanonical.RequireSha(value.GenerationSha256,
            "generationSha256");
        StatisticReconciliationIndependentReviewCanonical.RequireSha(value.SemanticVerdictSha256,
            "semanticVerdictSha256");
        StatisticReconciliationIndependentReviewCanonical.RequireSha(value.ReviewRecordSha256,
            "reviewRecordSha256");
        if (StatisticReconciliationIndependentReviewCanonical.ReviewRecordHash(value.ReviewRecord) !=
            value.ReviewRecordSha256 || value.ReviewRecord.Verdict != value.Verdict ||
            value.StateRevision < 0)
            throw StatisticReconciliationIndependentReviewCanonical.Invalid("generationBinding");
        if (requireSignable && (value.Verdict != "MATCHED" || !value.CompleteEvidence ||
                                !value.CoherentSnapshot || value.UnknownRootCause))
            throw Error(StatisticReconciliationIndependentReviewFailureCodes.TargetNotSignable,
                "matchedCompleteCoherentRequired");
    }

    private static void ValidateCommand(StatisticReconciliationReviewCommand? command,
        StatisticReconciliationReviewGeneration generation,
        bool requireStateRevision = true)
    {
        if (command is null)
            throw StatisticReconciliationIndependentReviewCanonical.Invalid("command");
        StatisticReconciliationIndependentReviewCanonical.RequireText(command.CommandId, "commandId");
        StatisticReconciliationIndependentReviewCanonical.RequireSha(command.RequestSha256,
            "requestSha256");
        if (StatisticReconciliationIndependentReviewCanonical.CommandHash(command) !=
            command.RequestSha256)
            throw StatisticReconciliationIndependentReviewCanonical.Invalid("requestHash");
        if (!StatisticReconciliationReviewGates.All.Contains(command.Gate) ||
            command.Decision is not (StatisticReconciliationReviewDecisions.Approve or
                StatisticReconciliationReviewDecisions.Reject))
            throw StatisticReconciliationIndependentReviewCanonical.Invalid("gateOrDecision");
        if (command.ReconciliationId != generation.ReconciliationId ||
            command.GenerationId != generation.GenerationId ||
            command.GenerationSha256 != generation.GenerationSha256 ||
            command.SemanticVerdictSha256 != generation.SemanticVerdictSha256)
            throw StatisticReconciliationIndependentReviewCanonical.Invalid("commandGenerationBinding");
        if (requireStateRevision &&
            command.ExpectedStateRevision != generation.StateRevision)
            throw Error(StatisticReconciliationIndependentReviewFailureCodes.StaleCas,
                "expectedStateRevision");
    }

    private static void ValidateSupersessionCommand(
        StatisticReconciliationReviewSupersessionCommand? command,
        StatisticReconciliationReviewGeneration generation,
        bool requireStateRevision = true)
    {
        if (command is null)
            throw StatisticReconciliationIndependentReviewCanonical.Invalid("supersessionCommand");
        StatisticReconciliationIndependentReviewCanonical.RequireText(command.CommandId, "commandId");
        StatisticReconciliationIndependentReviewCanonical.RequireSha(command.RequestSha256,
            "requestSha256");
        if (StatisticReconciliationIndependentReviewCanonical.SupersessionCommandHash(command) !=
            command.RequestSha256 || command.ReconciliationId != generation.ReconciliationId ||
            command.NewGenerationId != generation.GenerationId ||
            command.NewGenerationSha256 != generation.GenerationSha256 ||
            command.PreviousGenerationId == command.NewGenerationId)
            throw StatisticReconciliationIndependentReviewCanonical.Invalid("supersessionBinding");
        if (requireStateRevision &&
            command.ExpectedStateRevision != generation.StateRevision)
            throw Error(StatisticReconciliationIndependentReviewFailureCodes.StaleCas,
                "expectedStateRevision");
    }

    private static StatisticReconciliationIndependentReviewAuditRecord BuildDecision(
        StatisticReconciliationReviewPermission permission,
        StatisticReconciliationReviewGeneration generation,
        StatisticReconciliationReviewCommand command,
        DateTime at)
    {
        at = RequireUtc(at);
        var id = StatisticReconciliationIndependentReviewCanonical.Hash(
            "P10_REVIEW_DECISION_ID_V1", command.ReconciliationId, command.GenerationId,
            command.Gate, command.CommandId);
        var document = DocumentHash(id, StatisticReconciliationReviewRecordKinds.Decision,
            StatisticReconciliationReviewStatuses.Active, command.CommandId, command.RequestSha256,
            command.ReconciliationId, command.GenerationId, command.GenerationSha256,
            command.SemanticVerdictSha256, generation.ReviewRecordSha256, command.Gate,
            command.Decision, permission.ActorId, permission.PermissionSnapshotSha256,
            permission.RowCountBeforeRedaction, permission.RowCountAfterRedaction,
            command.ExpectedStateRevision, null, null, at);
        return new(id, StatisticReconciliationReviewRecordKinds.Decision,
            StatisticReconciliationReviewStatuses.Active, command.CommandId, command.RequestSha256,
            command.ReconciliationId, command.GenerationId, command.GenerationSha256,
            command.SemanticVerdictSha256, generation.ReviewRecordSha256, command.Gate,
            command.Decision, permission.ActorId, permission.PermissionSnapshotSha256,
            permission.RowCountBeforeRedaction, permission.RowCountAfterRedaction,
            command.ExpectedStateRevision, null, null, document, at);
    }

    private static StatisticReconciliationIndependentReviewAuditRecord BuildSupersession(
        StatisticReconciliationReviewPermission permission,
        StatisticReconciliationReviewGeneration generation,
        StatisticReconciliationReviewSupersessionCommand command,
        StatisticReconciliationIndependentReviewAuditRecord previous,
        DateTime at)
    {
        at = RequireUtc(at);
        var id = StatisticReconciliationIndependentReviewCanonical.Hash(
            "P10_REVIEW_SUPERSESSION_ID_V1", previous.Id, command.NewGenerationId,
            command.CommandId);
        var document = DocumentHash(id, StatisticReconciliationReviewRecordKinds.Supersession,
            StatisticReconciliationReviewStatuses.Superseded, command.CommandId,
            command.RequestSha256, command.ReconciliationId, previous.GenerationId,
            previous.GenerationSha256, previous.SemanticVerdictSha256,
            previous.ReviewRecordSha256, previous.Gate, null, permission.ActorId,
            permission.PermissionSnapshotSha256, permission.RowCountBeforeRedaction,
            permission.RowCountAfterRedaction, command.ExpectedStateRevision, previous.Id,
            generation.GenerationId, at);
        return new(id, StatisticReconciliationReviewRecordKinds.Supersession,
            StatisticReconciliationReviewStatuses.Superseded, command.CommandId,
            command.RequestSha256, command.ReconciliationId, previous.GenerationId,
            previous.GenerationSha256, previous.SemanticVerdictSha256,
            previous.ReviewRecordSha256, previous.Gate, null, permission.ActorId,
            permission.PermissionSnapshotSha256, permission.RowCountBeforeRedaction,
            permission.RowCountAfterRedaction, command.ExpectedStateRevision, previous.Id,
            generation.GenerationId, document, at);
    }

    internal static void ValidateStored(StatisticReconciliationIndependentReviewAuditRecord value)
    {
        var expected = DocumentHash(value.Id, value.RecordKind, value.Status,
            value.OperationCommandId, value.RequestSha256, value.ReconciliationId,
            value.GenerationId, value.GenerationSha256, value.SemanticVerdictSha256,
            value.ReviewRecordSha256, value.Gate, value.Decision, value.ReviewerActorId,
            value.PermissionSnapshotSha256, value.RowCountBeforeRedaction,
            value.RowCountAfterRedaction, value.StateRevision, value.SupersedesDecisionId,
            value.SupersededByGenerationId, value.CreatedAtUtc);
        if (expected != value.DocumentSha256)
            throw Error(StatisticReconciliationIndependentReviewFailureCodes.PersistenceInvalid,
                "documentHash");
    }

    private static string DocumentHash(params object?[] fields)
        => StatisticReconciliationIndependentReviewCanonical.Hash(
            ["P10_REVIEW_AUDIT_RECORD_V1", .. fields]);

    private static DateTime RequireUtc(DateTime value)
    {
        if (value == default || value.Kind != DateTimeKind.Utc)
            throw StatisticReconciliationIndependentReviewCanonical.Invalid("createdAtUtc");
        return value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMillisecond));
    }

    private static StatisticReconciliationIndependentReviewException Error(string code,
        string detail) => new(code, detail);
}
