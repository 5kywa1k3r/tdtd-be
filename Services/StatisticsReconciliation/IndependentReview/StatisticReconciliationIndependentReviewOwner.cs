using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;
using tdtd_be.Services.StatisticsReconciliation.Recheck;

namespace tdtd_be.Services.StatisticsReconciliation.IndependentReview;

public sealed record StatisticReconciliationReviewOwnerCommand(
    string CommandId,
    string Gate,
    string Decision,
    long ExpectedStateRevision);

public sealed record StatisticReconciliationReviewOwnerSupersessionCommand(
    string CommandId,
    string PreviousGenerationId,
    long ExpectedStateRevision);

public sealed record StatisticReconciliationReviewSubmissionDto(
    bool Replayed,
    string ReconciliationId,
    string GenerationId,
    string Gate,
    string Decision,
    string Status);

public sealed record StatisticReconciliationReviewSupersessionDto(
    string ReconciliationId,
    string PreviousGenerationId,
    string NewGenerationId,
    int SupersededApprovalCount);

public sealed class StatisticReconciliationReviewScopeAuthorization
{
    internal StatisticReconciliationReviewScopeAuthorization(
        string actorId,
        string actorUnitId,
        string workId,
        string scopeAssignmentId,
        IReadOnlyList<string> permissionCodes,
        string permissionSnapshotSha256,
        int rowCountBeforeRedaction,
        int rowCountAfterRedaction,
        bool canViewOperatorDetail)
    {
        ActorId = actorId;
        ActorUnitId = actorUnitId;
        WorkId = workId;
        ScopeAssignmentId = scopeAssignmentId;
        PermissionCodes = permissionCodes;
        PermissionSnapshotSha256 = permissionSnapshotSha256;
        RowCountBeforeRedaction = rowCountBeforeRedaction;
        RowCountAfterRedaction = rowCountAfterRedaction;
        CanViewOperatorDetail = canViewOperatorDetail;
    }

    public string ActorId { get; }
    public string ActorUnitId { get; }
    public string WorkId { get; }
    public string ScopeAssignmentId { get; }
    public IReadOnlyList<string> PermissionCodes { get; }
    public string PermissionSnapshotSha256 { get; }
    public int RowCountBeforeRedaction { get; }
    public int RowCountAfterRedaction { get; }
    public bool CanViewOperatorDetail { get; }
}

public interface IStatisticReconciliationIndependentReviewOwner
{
    Task<StatisticReconciliationReviewScopeAuthorization?> AuthorizeScopeAsync(
        string? workId,
        string? scopeAssignmentId,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatisticReconciliationReviewSubmissionDto> SubmitAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId,
        StatisticReconciliationReviewOwnerCommand command,
        CancellationToken ct = default);

    Task<StatisticReconciliationReviewReadResult> ReadAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId,
        CancellationToken ct = default);

    Task<StatisticReconciliationReviewFinalApproval> GetFinalApprovalAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId,
        CancellationToken ct = default);

    Task<StatisticReconciliationReviewSupersessionDto> SupersedeAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId,
        StatisticReconciliationReviewOwnerSupersessionCommand command,
        CancellationToken ct = default);
}

public sealed class StatisticReconciliationIndependentReviewOwner(
    MongoDbContext context,
    IStatisticReconciliationIndependentReviewCandidateGate candidateGate,
    IStatisticReconciliationReviewBackend verdictBackend,
    StatisticReconciliationIndependentReviewService reviewService,
    IStatisticReconciliationCurrentReviewValidator currentValidator,
    IAppTimeService time)
    : IStatisticReconciliationIndependentReviewOwner
{
    public async Task<StatisticReconciliationReviewScopeAuthorization?>
        AuthorizeScopeAsync(
            string? workId,
            string? scopeAssignmentId,
            MeResponse actor,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!CanReview(actor) || !ExactObjectId(workId) ||
            !ExactObjectId(scopeAssignmentId) || !ExactObjectId(actor.Id))
            return null;

        var filters = Builders<WorkAssignment>.Filter;
        var target = filters.Eq(value => value.Id, scopeAssignmentId!) &
                     filters.Eq(value => value.WorkId, workId!) &
                     filters.Eq(value => value.IsDeleted, false) &
                     filters.Eq(value => value.IsActive, true);
        if (!RoleGuard.IsAdmin(actor) && !RoleGuard.IsSystemAdmin(actor))
        {
            var authorized = new List<FilterDefinition<WorkAssignment>>
            {
                filters.Eq(value => value.CreatedByUserId, actor.Id),
                filters.AnyEq(value => value.LeaderWatcherUserIds, actor.Id),
                filters.ElemMatch(value => value.Assignees,
                    assignee => assignee.UserId == actor.Id)
            };
            if (RoleGuard.TryGetManagerUnit(actor, out var managerUnitId) &&
                ExactObjectId(managerUnitId))
            {
                authorized.Add(filters.Eq(value => value.IssuedByUnitId,
                    managerUnitId));
                authorized.Add(filters.AnyEq(value => value.TargetUnitIds,
                    managerUnitId));
            }
            target &= filters.Or(authorized);
        }

        // Missing and unauthorized targets are deliberately indistinguishable.
        // No reconciliation, verdict, review or candidate artifact is inspected
        // before this exact assignment authorization succeeds.
        var assignment = await context.WorkAssignments.Find(target)
            .Project(value => value.Id).FirstOrDefaultAsync(ct);
        if (assignment is null)
            return null;

        var permissionCodes = PermissionCodes(actor);
        const int before = 1;
        const int after = 1;
        var snapshot = PermissionSnapshot(actor.Id, actor.UnitId, workId!,
            scopeAssignmentId!, permissionCodes, before, after);
        return new(actor.Id, actor.UnitId, workId!, scopeAssignmentId!,
            permissionCodes, snapshot, before, after,
            RoleGuard.IsAdmin(actor) || RoleGuard.IsSystemAdmin(actor));
    }

    public async Task<StatisticReconciliationReviewSubmissionDto> SubmitAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId,
        StatisticReconciliationReviewOwnerCommand command,
        CancellationToken ct = default)
    {
        var current = await ResolveCurrentAsync(authorization,
            reconciliationId, StatisticReconciliationRouteRegistry.ReviewSubmit, ct);
        command = NormalizeCommand(command);
        var serviceCommand = new StatisticReconciliationReviewCommand(
            command.CommandId,
            new string('0', 64),
            current.Generation.ReconciliationId,
            current.Generation.GenerationId,
            current.Generation.GenerationSha256,
            current.Generation.SemanticVerdictSha256,
            command.Gate,
            command.Decision,
            command.ExpectedStateRevision);
        serviceCommand = serviceCommand with
        {
            RequestSha256 = StatisticReconciliationIndependentReviewCanonical
                .CommandHash(serviceCommand)
        };
        var result = await reviewService.SubmitAsync(current.Permission,
            current.Generation, serviceCommand, RequireUtc(time.UtcNow), ct);
        return new(result.Replayed, result.Decision.ReconciliationId,
            result.Decision.GenerationId, result.Decision.Gate,
            result.Decision.Decision!, result.Decision.Status);
    }

    public async Task<StatisticReconciliationReviewReadResult> ReadAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId,
        CancellationToken ct = default)
    {
        var current = await ResolveCurrentAsync(authorization,
            reconciliationId, StatisticReconciliationRouteRegistry.ReviewRead, ct);
        var result = await reviewService.ReadAsync(current.Permission,
            current.Generation, ct);
        var remainingGates = StatisticReconciliationReviewGates.All
            .Where(gate => result.Summary.GateStates.TryGetValue(
                               gate,
                               out var state) &&
                           state == "PENDING")
            .ToArray();
        var separated = !string.Equals(
                            current.Permission.ActorId,
                            current.Generation.RunInitiatorActorId,
                            StringComparison.Ordinal) &&
                        !string.Equals(
                            current.Permission.ActorId,
                            current.Generation.LatestGenerationWriterActorId,
                            StringComparison.Ordinal);
        var canSubmit = current.Permission.CanReview &&
                        separated &&
                        remainingGates.Length > 0;
        var supersessionGenerationIds = current.Generation.Verdict ==
                "MATCHED" &&
            current.Generation.CompleteEvidence &&
            current.Generation.CoherentSnapshot &&
            !current.Generation.UnknownRootCause &&
            current.Permission.CanReview && separated
            ? await reviewService.ReadSupersessionCandidatesAsync(
                current.Generation.ReconciliationId,
                current.Generation.GenerationId, ct)
            : [];
        return result with
        {
            Actions = new StatisticReconciliationReviewActionsDto(
                canSubmit,
                current.Generation.StateRevision,
                canSubmit ? remainingGates : [])
            {
                CanSupersede = supersessionGenerationIds.Count > 0,
                SupersessionGenerationIds = supersessionGenerationIds
            }
        };
    }

    public async Task<StatisticReconciliationReviewFinalApproval>
        GetFinalApprovalAsync(
            StatisticReconciliationReviewScopeAuthorization authorization,
            string reconciliationId,
            CancellationToken ct = default)
    {
        var current = await ResolveCurrentAsync(authorization,
            reconciliationId, StatisticReconciliationRouteRegistry.ReviewRead, ct);
        return await reviewService.GetFinalApprovalAsync(current.Permission,
            current.Generation, ct);
    }

    public async Task<StatisticReconciliationReviewSupersessionDto>
        SupersedeAsync(
            StatisticReconciliationReviewScopeAuthorization authorization,
            string reconciliationId,
            StatisticReconciliationReviewOwnerSupersessionCommand command,
            CancellationToken ct = default)
    {
        var current = await ResolveCurrentAsync(authorization,
            reconciliationId, StatisticReconciliationRouteRegistry.ReviewSupersede,
            ct);
        command = NormalizeSupersession(command, current.Generation.GenerationId);
        var serviceCommand = new StatisticReconciliationReviewSupersessionCommand(
            command.CommandId,
            new string('0', 64),
            current.Generation.ReconciliationId,
            command.PreviousGenerationId,
            current.Generation.GenerationId,
            current.Generation.GenerationSha256,
            command.ExpectedStateRevision);
        serviceCommand = serviceCommand with
        {
            RequestSha256 = StatisticReconciliationIndependentReviewCanonical
                .SupersessionCommandHash(serviceCommand)
        };
        var markers = await reviewService.SupersedeApprovalsAsync(
            current.Permission, current.Generation, serviceCommand,
            RequireUtc(time.UtcNow), ct);
        return new(current.Generation.ReconciliationId,
            command.PreviousGenerationId, current.Generation.GenerationId,
            markers.Count);
    }

    private async Task<CurrentReviewTarget> ResolveCurrentAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId,
        string routeId,
        CancellationToken ct)
    {
        RequireAuthorization(authorization);
        var binding = candidateGate.Require(routeId);
        var normalizedReconciliationId = ExactObjectId(reconciliationId)
            ? reconciliationId
            : throw PermissionDenied();
        var run = await context.StatisticReconciliationRuns.Find(value =>
                value.Id == normalizedReconciliationId &&
                value.WorkId == authorization.WorkId &&
                value.ScopeAssignmentId == authorization.ScopeAssignmentId &&
                value.CandidateChainId == binding.ChainId &&
                !value.IsDeleted)
            .FirstOrDefaultAsync(ct) ?? throw PermissionDenied();
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(run);
        if (run.Recheck?.Phase ==
            StatisticReconciliationRecheckPhases
                .ReviewSupersessionPending)
            throw TargetNotSignable();
        if (run.CurrentGenerationId is null || run.CurrentGenerationHash is null)
            throw TargetNotSignable();

        var lineage = (await verdictBackend.ReadLineageAsync(run.Id, ct)).ToArray();
        foreach (var item in lineage)
            StatisticReconciliationFinalVerdictPublisher.ValidateStored(item);
        // A successor verdict is append-only and may be durable before the run
        // owner wins the pending-to-current CAS. The run's current actual
        // generation remains the visibility authority during that crash
        // window; a successor for a different actual generation must not hide
        // the still-current verdict.
        var candidates = lineage.Where(item =>
                item.ActualGenerationId == run.CurrentGenerationId &&
                item.ActualGenerationSha256 == run.CurrentGenerationHash)
            .ToArray();
        if (candidates.Length != 1)
            throw TargetNotSignable();
        var verdict = candidates[0];
        StatisticReconciliationCurrentReviewValidation validation;
        try
        {
            validation = await currentValidator.ValidateAsync(
                    run, verdict, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Current-owner, expected-owner and lifecycle details remain
            // opaque to reviewers. Nothing in the review ledger is read or
            // written after current evidence becomes unsignable.
            throw TargetNotSignable();
        }
        var confirmedRuns = await context.StatisticReconciliationRuns.Find(value =>
                value.Id == normalizedReconciliationId &&
                value.WorkId == authorization.WorkId &&
                value.ScopeAssignmentId == authorization.ScopeAssignmentId &&
                value.CandidateChainId == binding.ChainId &&
                !value.IsDeleted)
            .Limit(2).ToListAsync(ct).ConfigureAwait(false);
        if (confirmedRuns.Count != 1)
            throw TargetNotSignable();
        var confirmedRun = confirmedRuns[0];
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            confirmedRun);
        var confirmedLineage = (await verdictBackend
            .ReadLineageAsync(confirmedRun.Id, ct)).ToArray();
        foreach (var item in confirmedLineage)
            StatisticReconciliationFinalVerdictPublisher.ValidateStored(item);
        var confirmedCandidates = confirmedLineage.Where(item =>
                item.ActualGenerationId == confirmedRun.CurrentGenerationId &&
                item.ActualGenerationSha256 == confirmedRun.CurrentGenerationHash)
            .ToArray();
        if (confirmedCandidates.Length != 1 ||
            !ExactCurrentReplay(run, verdict, confirmedRun,
                confirmedCandidates[0]))
            throw TargetNotSignable();
        var permission = BuildPermission(authorization);
        var generation = BuildGeneration(
            confirmedRun, confirmedCandidates[0], permission, validation);
        return new(generation, permission);
    }

    internal static bool ExactCurrentReplay(
        StatisticReconciliationRun firstRun,
        StatisticReconciliationReview firstVerdict,
        StatisticReconciliationRun confirmedRun,
        StatisticReconciliationReview confirmedVerdict)
    {
        ArgumentNullException.ThrowIfNull(firstRun);
        ArgumentNullException.ThrowIfNull(firstVerdict);
        ArgumentNullException.ThrowIfNull(confirmedRun);
        ArgumentNullException.ThrowIfNull(confirmedVerdict);
        // Compare the exact persisted BSON, not a hand-selected field subset.
        // Any run or verdict mutation after current validation therefore closes
        // the target before review-ledger access, including recheck bindings and
        // fields that feed the eight-column review generation.
        return firstRun.ToBson().SequenceEqual(confirmedRun.ToBson()) &&
            firstVerdict.ToBson().SequenceEqual(confirmedVerdict.ToBson());
    }
    private static StatisticReconciliationReviewGeneration BuildGeneration(
        StatisticReconciliationRun run,
        StatisticReconciliationReview verdict,
        StatisticReconciliationReviewPermission permission,
        StatisticReconciliationCurrentReviewValidation validation)
    {
        run = StatisticReconciliationRecheckCaptureBindingCanonical
            .EffectiveCurrentRun(run);
        if (verdict.ReconciliationId != run.Id ||
            verdict.ActualGenerationId != run.CurrentGenerationId ||
            verdict.ActualGenerationSha256 != run.CurrentGenerationHash)
            throw TargetNotSignable();
        StatisticReconciliationIndependentReviewCanonical.RequireSha(
            validation.ValidationSha256, "currentValidationSha256");
        var permissionColumn =
            StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_REVIEW_PERMISSION_COLUMN_V1",
                verdict.AuthorizationEvidenceSha256,
                permission.ActorId,
                permission.PermissionSnapshotSha256,
                permission.RowCountBeforeRedaction,
                permission.RowCountAfterRedaction);
        var record = new StatisticReconciliationEightColumnRecord(
            StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_REVIEW_IDENTITY_COLUMN_V1", run.ImmutableIdentityHash,
                run.ImmutableHeaderHash),
            StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_REVIEW_CONFIG_COLUMN_V1", run.P8ConfigBundleHash,
                run.ActualConfigurationBundleSha256,
                run.CandidateCatalogSemanticSha256,
                run.CandidateSchemaSemanticSha256),
            StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_REVIEW_EXPECTED_COLUMN_V1", verdict.ExpectedGenerationId,
                verdict.ExpectedGenerationSha256),
            StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_REVIEW_ACTUAL_COLUMN_V1", verdict.ActualGenerationId,
                verdict.ActualGenerationSha256),
            verdict.DeltaManifestSha256,
            StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_REVIEW_FRESHNESS_COLUMN_V2",
                verdict.FreshnessAssessmentSha256 ??
                    StatisticReconciliationIndependentReviewCanonical.Hash(
                        "P10_REVIEW_FRESHNESS_NONE_V1"),
                validation.ValidationSha256),
            permissionColumn,
            verdict.Verdict);
        var recordSha = StatisticReconciliationIndependentReviewCanonical
            .ReviewRecordHash(record);
        return new(run.Id, verdict.VerdictGenerationId,
            verdict.VerdictGenerationSha256, verdict.DocumentSemanticSha256,
            record, recordSha, verdict.Verdict, verdict.CompleteEvidence,
            verdict.Signable && verdict.CloseoutAllowed &&
            verdict.AllRequiredLayersZero,
            verdict.UnknownBlocksCloseout ||
            verdict.RootCauseClass == StatisticReconciliationRootCauseClasses.Unknown,
            run.ActorUserId, run.LatestWriterUserId, run.StateRevision);
    }

    private static StatisticReconciliationReviewPermission BuildPermission(
        StatisticReconciliationReviewScopeAuthorization authorization)
        => new(true, true, true, true,
            authorization.CanViewOperatorDetail, authorization.ActorId,
            authorization.PermissionSnapshotSha256,
            authorization.RowCountBeforeRedaction,
            authorization.RowCountAfterRedaction);

    private static StatisticReconciliationReviewOwnerCommand NormalizeCommand(
        StatisticReconciliationReviewOwnerCommand? command)
    {
        if (command is null || string.IsNullOrWhiteSpace(command.CommandId) ||
            command.CommandId.Trim().Length > 128 ||
            !StatisticReconciliationReviewGates.All.Contains(command.Gate) ||
            command.Decision is not (
                StatisticReconciliationReviewDecisions.Approve or
                StatisticReconciliationReviewDecisions.Reject) ||
            command.ExpectedStateRevision < 0)
            throw StatisticReconciliationIndependentReviewCanonical.Invalid(
                "command");
        return command with { CommandId = command.CommandId.Trim() };
    }

    private static StatisticReconciliationReviewOwnerSupersessionCommand
        NormalizeSupersession(
            StatisticReconciliationReviewOwnerSupersessionCommand? command,
            string currentGenerationId)
    {
        if (command is null || string.IsNullOrWhiteSpace(command.CommandId) ||
            command.CommandId.Trim().Length > 128 ||
            command.ExpectedStateRevision < 0 ||
            command.PreviousGenerationId == currentGenerationId)
            throw StatisticReconciliationIndependentReviewCanonical.Invalid(
                "supersessionCommand");
        StatisticReconciliationIndependentReviewCanonical.RequireSha(
            command.PreviousGenerationId, "previousGenerationId");
        return command with { CommandId = command.CommandId.Trim() };
    }

    private static void RequireAuthorization(
        StatisticReconciliationReviewScopeAuthorization? value)
    {
        if (value is null || !ExactObjectId(value.ActorId) ||
            !ExactObjectId(value.WorkId) ||
            !ExactObjectId(value.ScopeAssignmentId) ||
            value.RowCountBeforeRedaction != 1 ||
            value.RowCountAfterRedaction != 1 ||
            value.PermissionCodes.Count == 0 ||
            !value.PermissionCodes.SequenceEqual(
                value.PermissionCodes.OrderBy(item => item,
                    StringComparer.Ordinal), StringComparer.Ordinal) ||
            PermissionSnapshot(value.ActorId, value.ActorUnitId,
                value.WorkId, value.ScopeAssignmentId,
                value.PermissionCodes, value.RowCountBeforeRedaction,
                value.RowCountAfterRedaction) !=
            value.PermissionSnapshotSha256)
            throw PermissionDenied();
    }

    internal static IReadOnlyList<string> PermissionCodes(MeResponse actor)
    {
        var values = new HashSet<string>(StringComparer.Ordinal)
        {
            "STAT_RECONCILIATION_REVIEW"
        };
        foreach (var role in actor.Roles ?? [])
            if (!string.IsNullOrWhiteSpace(role))
                values.Add("ROLE:" + role.Trim().ToUpperInvariant());
        return values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    internal static string PermissionSnapshot(string actorId, string unitId,
        string workId, string scopeAssignmentId,
        IReadOnlyList<string> permissionCodes, int before, int after)
        => StatisticReconciliationIndependentReviewCanonical.Hash(
            "P10_REVIEW_PERMISSION_SNAPSHOT_V2", actorId, unitId, workId,
            scopeAssignmentId, string.Join("\n", permissionCodes), before, after);

    private static bool ExactObjectId(string? value)
        => ObjectId.TryParse(value, out var parsed) &&
           string.Equals(parsed.ToString(), value, StringComparison.Ordinal);

    private static bool CanReview(MeResponse actor)
        => !actor.IsDeleted && (RoleGuard.IsSystemAdmin(actor) ||
                               RoleGuard.IsAdmin(actor) ||
                               RoleGuard.IsManagerLevel(actor) ||
                               RoleGuard.TryGetManagerUnit(actor, out _));

    private static DateTime RequireUtc(DateTime value)
    {
        if (value == default)
            throw StatisticReconciliationIndependentReviewCanonical.Invalid(
                "createdAtUtc");
        return value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
    }

    private static StatisticReconciliationIndependentReviewException
        PermissionDenied()
        => new(StatisticReconciliationIndependentReviewFailureCodes.PermissionDenied,
            "opaqueTarget");

    private static StatisticReconciliationIndependentReviewException
        TargetNotSignable()
        => new(StatisticReconciliationIndependentReviewFailureCodes.TargetNotSignable,
            "matchedCompleteCoherentRequired");

    private sealed record CurrentReviewTarget(
        StatisticReconciliationReviewGeneration Generation,
        StatisticReconciliationReviewPermission Permission);
}
