using System.Text.Json;
using MongoDB.Bson;
using Microsoft.Extensions.Caching.Memory;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.StatisticsReconciliation.Recheck;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService : IStatisticReconciliationRunService
{
    private readonly MongoDbContext _ctx;
    private readonly IStatisticReconciliationCandidateActivation _activation;
    private readonly IStatRunCandidateActivation _statRunActivation;
    private readonly IAppTimeService _time;
    private readonly int _maxRetryCount;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _retryBaseDelay;
    private readonly TimeSpan _retryMaxDelay;
    private readonly TimeSpan _fixtureSla;
    private readonly byte[] _expectedObservationCursorSigningKey;
    private readonly byte[] _capturePlanTokenSigningKey;    private readonly IMemoryCache _expectedObservationIntegrityCache;


    public StatisticReconciliationRunService(
        MongoDbContext ctx,
        IStatisticReconciliationCandidateActivation activation,
        IStatRunCandidateActivation statRunActivation,
        IAppTimeService time,
        IConfiguration configuration,
        IMemoryCache expectedObservationIntegrityCache)
    {
        _ctx = ctx;
        _activation = activation;
        _statRunActivation = statRunActivation;
        _time = time;
        _expectedObservationIntegrityCache = expectedObservationIntegrityCache;
        _maxRetryCount = Math.Clamp(
            configuration.GetValue<int?>("StatisticReconciliation:MaxRetryCount") ?? 5,
            1,
            20);
        _leaseDuration = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>("StatisticReconciliation:LeaseSeconds") ?? 30,
            15,
            600));
        _retryBaseDelay = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>("StatisticReconciliation:RetryBaseSeconds") ?? 2,
            1,
            60));
        _retryMaxDelay = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>("StatisticReconciliation:RetryMaxSeconds") ?? 60,
            5,
            600));
        _fixtureSla = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>("StatisticReconciliation:SlaSeconds") ?? 600,
            30,
            3600));
        var cursorSigningKey =
            configuration["StatisticReconciliation:CursorSigningKey"] ??
            configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(cursorSigningKey))
            throw new InvalidOperationException(
                "Statistic reconciliation cursor signing key is missing.");
        _expectedObservationCursorSigningKey =
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(cursorSigningKey));
        _capturePlanTokenSigningKey =
            StatisticReconciliationCapturePlanToken.DeriveSigningKey(
                configuration["StatisticReconciliation:CapturePlanTokenSigningKey"] ??
                cursorSigningKey);

    }

    public async Task<StatisticReconciliationCreateResult> CreateAsync(
        string workId,
        string scopeAssignmentId,
        JsonElement body,
        MeResponse actor,
        CancellationToken ct = default)
    {
        // Coarse role/scope authorization deliberately precedes hidden P9 ids,
        // source integrity, candidate diagnostics and receipt existence.
        var scope = await AuthorizeScopeAsync(workId, scopeAssignmentId, actor, ct);
        var request = StatisticReconciliationCanonicalJson
            .DeserializeStrict<StatisticReconciliationCreateRequest>(body);
        var normalized = NormalizeCreate(request, actor);
        var p9 = await LoadP9PublicationAsync(normalized, scope, actor, ct);
        var binding = _activation.RequireFoundation(
            StatisticReconciliationCapabilities.SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry.Create);

        var receiptId = BuildReceiptId(
            actor,
            scope.WorkId,
            scope.Id,
            normalized.CommandId);
        var existing = await _ctx.StatisticReconciliationRuns
            .Find(x => x.ReceiptId == receiptId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            RequireReadIntegrity(existing);
            if (normalized.CapturePlanToken is not null)
            {
                var replayToken = ValidateCapturePlanToken(
                    normalized.CapturePlanToken,
                    MongoUtcNow(),
                    allowExpiredReplay: true);
                RequireCapturePlanTokenRequestBinding(
                    replayToken,
                    normalized,
                    scope,
                    p9,
                    actor);
                RequireCapturePlanTokenReplayBinding(replayToken, existing);
                normalized = normalized with
                {
                    ActualCapturePlan =
                        BuildDirectOnlyCapturePlanRequest(
                            p9,
                            replayToken.ExportId),
                    CapturePlanToken = null
                };
            }
            var replayBinding = BuildCreateRequestBinding(
                scope.WorkId,
                scope.Id,
                normalized,
                existing.ActualCapturePlanSha256,
                existing.ActualConfigurationBundleSha256);
            var replayRequestHash =
                StatisticReconciliationCanonicalJson.HashObject(replayBinding);
            return ResolveCreateReplay(
                existing,
                replayRequestHash,
                binding.ChainId,
                actor);
        }

        StatisticReconciliationActualCapturePlan? actualCapturePlan;
        if (normalized.CapturePlanToken is not null)
        {
            var planToken = ValidateCapturePlanToken(
                normalized.CapturePlanToken,
                MongoUtcNow(),
                allowExpiredReplay: false);
            RequireCapturePlanTokenRequestBinding(
                planToken,
                normalized,
                scope,
                p9,
                actor);
            var tokenPlanRequest = BuildDirectOnlyCapturePlanRequest(
                p9,
                planToken.ExportId);
            actualCapturePlan = await ResolveActualCapturePlanAsync(
                tokenPlanRequest,
                p9,
                scope,
                actor,
                normalized.ConceptKey,
                normalized.FilterHash,
                normalized.CanonicalFilterJson ?? string.Empty,
                ct);
            if (actualCapturePlan is null)
                throw RequestInvalid("capturePlanToken", "SERVER_PLAN_REQUIRED");
            RequireDirectOnlyPlan(
                actualCapturePlan,
                p9,
                planToken.ExportId);
            RequireCapturePlanTokenResolvedBinding(
                planToken,
                actualCapturePlan);
            normalized = normalized with
            {
                ActualCapturePlan = tokenPlanRequest,
                CapturePlanToken = null
            };
        }
        else
        {
            if (normalized.ActualCapturePlan is null)
            {
                throw RequestInvalid(
                    "actualCapturePlan",
                    "ACTUAL_CAPTURE_PLAN_REQUIRED");
            }
            actualCapturePlan = await ResolveActualCapturePlanAsync(
                normalized.ActualCapturePlan,
                p9,
                scope,
                actor,
                normalized.ConceptKey,
                normalized.FilterHash,
                normalized.CanonicalFilterJson ?? string.Empty,
                ct);
            if (actualCapturePlan is null)
                throw RequestInvalid("actualCapturePlan", "SERVER_PLAN_REQUIRED");
        }        var requestBinding = BuildCreateRequestBinding(
            scope.WorkId,
            scope.Id,
            normalized,
            actualCapturePlan?.PlanSha256,
            actualCapturePlan?.ActualConfigurationBundleSha256);
        var requestHash =
            StatisticReconciliationCanonicalJson.HashObject(requestBinding);
        var now = MongoUtcNow();
        var permissionCodes = BuildPermissionCodes(actor);
        var operationHistoryHash = BuildOperationReceiptHistoryHash(
            Array.Empty<StatisticReconciliationOperationReceipt>());
        var run = new StatisticReconciliationRun
        {
            Id = ObjectId.GenerateNewId().ToString(),
            ReceiptId = receiptId,
            CommandId = normalized.CommandId,
            RequestHash = requestHash,
            ActorUserId = actor.Id,
            TenantUnitId = NormalizeTenantId(actor.UnitId),
            PermissionCodes = permissionCodes.ToList(),
            AuthorizationSnapshotHash = BuildAuthorizationSnapshotHash(
                actor,
                permissionCodes,
                scope),
            WorkId = scope.WorkId,
            ScopeAssignmentId = scope.Id,
            P9ResultKind = normalized.P9ResultKind,
            P9ResultId = p9.Id,
            P9RunId = p9.Id,
            P9GenerationId = p9.GenerationId!,
            P9GenerationHash = p9.GenerationHash!,
            P9RunKind = p9.RunKind!,
            P9CapabilityId = p9.CapabilityId!,
            P9RouteId = p9.RouteId!,
            P9CandidateChainId = p9.CandidateChainId!,
            P9CandidatePromptId = p9.CandidatePromptId!,
            SourceReportId = p9.SourceReportId!,
            SourcePayloadRevision = p9.SourcePayloadRevision!.Value,
            SourcePayloadHash = p9.SourcePayloadHash!,
            SourceLifecycleRevision = p9.SourceLifecycleRevision!.Value,
            SourceLifecycleEventKey = p9.SourceLifecycleEventKey,
            SourceLifecycleHash = BuildSourceLifecycleHash(p9),
            SourceLifecycleStatus = p9.SourceStatus ?? string.Empty,
            DynamicFormFamilyId = p9.DynamicFormFamilyId,
            DynamicFormVersionId = p9.DynamicFormTemplateId,
            DynamicFormVersionNo = p9.DynamicFormVersionNo,
            DynamicFormSchemaHash = p9.DynamicFormSchemaHash,
            FlowTemplateId = p9.FlowTemplateId,
            FlowFamilyRevision = p9.FlowFamilyRevision,
            FlowTemplateVersionId = p9.FlowTemplateVersionId,
            FlowPayloadHash = p9.FlowPayloadHash,
            FlowInstanceId = p9.FlowInstanceId,
            FlowInstanceRevision = p9.FlowInstanceRevision,
            FlowExecutionEpoch = p9.FlowExecutionEpoch,
            FlowExecutionEpochId = p9.FlowExecutionEpochId,
            FlowExecutionEpochRevision = p9.FlowExecutionEpochRevision,
            FlowStepId = p9.FlowStepId,
            FlowBranchId = p9.FlowBranchId,
            FlowStepInstanceId = p9.FlowStepInstanceId,
            FlowStepInstanceRevision = p9.FlowStepInstanceRevision,
            FlowContributionPolicy = p9.FlowContributionPolicy,
            FlowContributionPolicyHash = p9.FlowContributionPolicyHash,
            FlowEffectiveStatus = p9.FlowEffectiveStatus,
            FlowContributionProvenanceHash = DeriveContributionProvenanceHash(p9),
            P8ConfigOwnerId = p9.DynamicFormTemplateId,
            P8ConfigId = p9.ConfigId,
            P8ConfigVersionId = p9.ConfigVersionId,
            P8ConfigVersionNo = p9.ConfigVersionNo,
            P8ConfigRevision = p9.ConfigRevision,
            P8ConfigHash = p9.ConfigHash,
            P8ConfigBundleHash = BuildP8ConfigBundleHash(p9),
            P9CatalogVersion = p9.CatalogVersion!,
            P9CatalogRawSha256 = p9.CatalogRawSha256!,
            P9CatalogSemanticSha256 = p9.CatalogSemanticSha256!,
            P9SchemaRawSha256 = p9.SchemaRawSha256!,
            P9SchemaSemanticSha256 = p9.SchemaSemanticSha256!,
            P9StageLockSha256 = p9.StageLockSha256!,
            CandidateChainId = binding.ChainId,
            CandidatePromptId = binding.PromptId,
            CandidateStage = binding.Stage,
            CandidateCatalogVersion = binding.CatalogVersion,
            CandidateCatalogRawSha256 = binding.CatalogRawSha256,
            CandidateCatalogSemanticSha256 = binding.CatalogSemanticSha256,
            CandidateSchemaRawSha256 = binding.SchemaRawSha256,
            CandidateSchemaSemanticSha256 = binding.SchemaSemanticSha256,
            CandidateStageLockSha256 = binding.StageLockSha256,
            PeriodKey = p9.PeriodKey!,
            PeriodInstanceKey = p9.PeriodInstanceKey!,
            PeriodKind = p9.PeriodKind!,
            PeriodStartUtc = NormalizeUtc(p9.PeriodStartUtc),
            PeriodEndUtc = NormalizeUtc(p9.PeriodEndUtc),
            ConceptKey = normalized.ConceptKey,
            Grain = normalized.Grain,
            TimeAxis = DeriveTimeAxis(p9, normalized.Grain),
            PageContractHash = BuildPageContractHash(),
            FilterHash = normalized.FilterHash,
            CanonicalFilterJson = normalized.CanonicalFilterJson,
            ActualCapturePlan = actualCapturePlan,
            ActualCapturePlanSha256 = actualCapturePlan?.PlanSha256,
            ActualConfigurationBundleSha256 =
                actualCapturePlan?.ActualConfigurationBundleSha256,
            SourceSetSha256 = null,
            ExpectedAlgorithmRevision = null,
            ExpectedAlgorithmSha256 = null,
            Status = StatisticReconciliationRunStatuses.Queued,
            StateRevision = 1,
            RetryCount = 0,
            MaxRetryCount = _maxRetryCount,
            NextRetryAtUtc = now,
            InitialDeadlineAtUtc = now.Add(_fixtureSla),
            DeadlineAtUtc = now.Add(_fixtureSla),
            GenerationPublishRevision = 0,
            OperationReceiptHistoryHash = operationHistoryHash,
            OperationReceipts = [],
            LatestWriterUserId = actor.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
            UpdatedByUserId = actor.Id,
            IsDeleted = false
        };
        run.ImmutableIdentityHash = BuildImmutableIdentityHash(run);
        run.ImmutableHeaderHash = BuildImmutableHeaderHash(run);
        run.ReceiptResponseHash = BuildAcceptedResponseHash(run);
        run.StateHash = BuildStateHash(run.Id, StateOf(run));

        try
        {
            await _ctx.StatisticReconciliationRuns.InsertOneAsync(
                run,
                cancellationToken: ct);
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            existing = await _ctx.StatisticReconciliationRuns
                .Find(x => x.ReceiptId == receiptId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
                return ResolveCreateReplay(existing, requestHash, binding.ChainId, actor);

            var identityCollision = await _ctx.StatisticReconciliationRuns
                .Find(x => x.ImmutableIdentityHash == run.ImmutableIdentityHash &&
                           !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (identityCollision is not null)
                throw IdentityConflict("IMMUTABLE_RECONCILIATION_ALREADY_EXISTS");
            throw IdentityConflict("DUPLICATE_KEY_INTEGRITY_CONFLICT");
        }

        return new StatisticReconciliationCreateResult(false, ToSummary(run));
    }

    public async Task<StatisticReconciliationPageResponse> ListAsync(
        string workId,
        string scopeAssignmentId,
        int page,
        int pageSize,
        MeResponse actor,
        CancellationToken ct = default)
    {
        var scope = await AuthorizeScopeAsync(workId, scopeAssignmentId, actor, ct);
        if (page < 1)
            throw RequestInvalid("page", "PAGE_OUT_OF_RANGE");
        if (pageSize is < 1 or > 200)
            throw RequestInvalid("pageSize", "PAGE_SIZE_OUT_OF_RANGE");
        var binding = _activation.RequireFoundation(
            StatisticReconciliationCapabilities.SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry.List);
        var filter = Builders<StatisticReconciliationRun>.Filter.Eq(x => x.WorkId, scope.WorkId) &
                     Builders<StatisticReconciliationRun>.Filter.Eq(x => x.ScopeAssignmentId, scope.Id) &
                     Builders<StatisticReconciliationRun>.Filter.Eq(x => x.CandidateChainId, binding.ChainId) &
                     Builders<StatisticReconciliationRun>.Filter.Eq(x => x.IsDeleted, false);
        var total = await _ctx.StatisticReconciliationRuns.CountDocumentsAsync(
            filter,
            cancellationToken: ct);
        var rows = await _ctx.StatisticReconciliationRuns
            .Find(filter)
            .SortByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
        var refreshedRows = new List<StatisticReconciliationRun>(rows.Count);
        foreach (var row in rows)
        {
            RequireReadIntegrity(row);
            var refreshed = await RefreshTimeoutAsync(row, actor.Id, ct);
            refreshedRows.Add(refreshed);
        }
        var presentations = await BuildPresentationsAsync(refreshedRows, actor, ct);
        var result = refreshedRows.Select(row =>
            ToSummary(row, presentations[row.Id])).ToArray();
        return new StatisticReconciliationPageResponse
        {
            Rows = result,
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<StatisticReconciliationSummaryResponse> GetSummaryAsync(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        MeResponse actor,
        CancellationToken ct = default)
    {
        var scope = await AuthorizeScopeAsync(workId, scopeAssignmentId, actor, ct);
        var binding = _activation.RequireFoundation(
            StatisticReconciliationCapabilities.SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry.Read);
        var run = await LoadAuthorizedRunAsync(
            scope.WorkId,
            scope.Id,
            reconciliationId,
            binding.ChainId,
            actor,
            ct);
        RequireReadIntegrity(run);
        run = await RefreshTimeoutAsync(run, actor.Id, ct);
        var presentations = await BuildPresentationsAsync([run], actor, ct);
        return ToSummary(run, presentations[run.Id]);
    }

    public async Task<StatisticReconciliationDetailResponse> GetDetailAsync(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RequireDetailRole(actor);
        var scope = await AuthorizeScopeAsync(workId, scopeAssignmentId, actor, ct);
        var binding = _activation.RequireFoundation(
            StatisticReconciliationCapabilities.SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry.Read);
        var run = await LoadAuthorizedRunAsync(
            scope.WorkId,
            scope.Id,
            reconciliationId,
            binding.ChainId,
            actor,
            ct);
        RequireReadIntegrity(run);
        run = await RefreshTimeoutAsync(run, actor.Id, ct);
        var presentations = await BuildPresentationsAsync([run], actor, ct);
        var effectiveRun =
            StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCurrentRun(run);
        var currentP8 = run.CurrentRecheckFinalizeReceipt?
            .CurrentP8Configuration;
        if (currentP8 is not null)
        {
            StatisticReconciliationTrustedP8ConfigurationIdentityCanonical
                .RequireValid(currentP8);
            effectiveRun.P8ConfigOwnerId = currentP8.OwnerId;
            effectiveRun.P8ConfigId = currentP8.ConfigId;
            effectiveRun.P8ConfigVersionId = currentP8.VersionId;
            effectiveRun.P8ConfigVersionNo = currentP8.VersionNo;
            effectiveRun.P8ConfigRevision = currentP8.Revision;
            effectiveRun.P8ConfigHash = currentP8.ConfigHash;
            effectiveRun.P8ConfigBundleHash = currentP8.BundleSha256;
        }
        return ToDetail(effectiveRun, presentations[run.Id]);
    }

    public async Task<StatisticReconciliationSummaryResponse> CancelAsync(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        JsonElement body,
        MeResponse actor,
        CancellationToken ct = default)
    {
        var scope = await AuthorizeScopeAsync(workId, scopeAssignmentId, actor, ct);
        var request = StatisticReconciliationCanonicalJson
            .DeserializeStrict<StatisticReconciliationCancelRequest>(body);
        var binding = _activation.RequireFoundation(
            StatisticReconciliationCapabilities.SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry.Cancel);
        var commandId = NormalizeCommandId(request.CommandId);
        if (request.ExpectedStateRevision < 1)
            throw RequestInvalid("expectedStateRevision", "REVISION_INVALID");
        if (!StatisticReconciliationCanonicalJson.IsCanonicalSha256(request.ExpectedStateHash))
            throw RequestInvalid("expectedStateHash", "SHA256_CANONICAL_INVALID");
        var run = await LoadAuthorizedRunAsync(
            scope.WorkId,
            scope.Id,
            reconciliationId,
            binding.ChainId,
            actor,
            ct);
        RequireReadIntegrity(run);
        run = await RefreshTimeoutAsync(run, actor.Id, ct);
        RequireReadIntegrity(run);
        ThrowIfTimedOut(run);
        var requestHash = StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_RECONCILIATION_CANCEL_REQUEST_V1",
            reconciliationId = run.Id,
            actorUserId = actor.Id,
            commandId,
            request.ExpectedStateRevision,
            request.ExpectedStateHash
        });
        var replay = (run.OperationReceipts ?? []).SingleOrDefault(receipt =>
            string.Equals(receipt.ActorUserId, actor.Id, StringComparison.Ordinal) &&
            string.Equals(receipt.Operation, "CANCEL", StringComparison.Ordinal) &&
            string.Equals(receipt.CommandId, commandId, StringComparison.Ordinal));
        if (replay is not null)
        {
            if (!string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal))
                throw ReplayMismatch("CANCEL_REQUEST_HASH_MISMATCH");
            return ToSummary(run, true);
        }
        if ((run.OperationReceipts?.Count ?? 0) >= MaxOperationReceipts)
            throw JobConflict("OPERATION_RECEIPT_LIMIT_REACHED");
        if (run.StateRevision != request.ExpectedStateRevision ||
            !string.Equals(run.StateHash, request.ExpectedStateHash, StringComparison.Ordinal))
        {
            throw RevisionConflict("STATE_CAS_LOST");
        }
        if (run.Status is not (
                StatisticReconciliationRunStatuses.Queued or
                StatisticReconciliationRunStatuses.Running))
        {
            throw JobConflict("JOB_NOT_CANCELLABLE");
        }

        var now = MongoUtcNow();
        var receipt = new StatisticReconciliationOperationReceipt
        {
            ReceiptId = StatisticReconciliationCanonicalJson.HashText(string.Join(
                "\n",
                "P10_RECONCILIATION_OPERATION_RECEIPT_V1",
                run.Id,
                actor.Id,
                "CANCEL",
                commandId)),
            ReconciliationId = run.Id,
            ActorUserId = actor.Id,
            Operation = "CANCEL",
            CommandId = commandId,
            RequestHash = requestHash,
            ExpectedStateRevision = request.ExpectedStateRevision,
            ExpectedStateHash = request.ExpectedStateHash,
            AcceptedStateRevision = run.StateRevision + 1,
            AcceptedStatus = StatisticReconciliationRunStatuses.Cancelled,
            AcceptedAtUtc = now
        };
        receipt.ReceiptHash = BuildOperationReceiptHash(receipt);
        var receipts = (run.OperationReceipts ?? []).Append(receipt).ToArray();
        var historyHash = BuildOperationReceiptHistoryHash(receipts);
        var nextState = StateOf(run) with
        {
            Status = StatisticReconciliationRunStatuses.Cancelled,
            StateRevision = run.StateRevision + 1,
            NextRetryAtUtc = null,
            LeaseOwnerId = null,
            ClaimToken = null,
            LeaseUntilUtc = null,
            LastHeartbeatAtUtc = null,
            DiagnosticCode = null,
            OperationReceiptHistoryHash = historyHash,
            CancelledAtUtc = now
        };
        var nextStateHash = BuildStateHash(run.Id, nextState);
        // The durable receipt binds the state that actually won the cancel CAS.
        // ReceiptHash intentionally excludes AcceptedStateHash so the state hash can
        // include the receipt-history hash without creating a hash cycle.
        receipt.AcceptedStateHash = nextStateHash;
        var fb = Builders<StatisticReconciliationRun>.Filter;
        var updated = await _ctx.StatisticReconciliationRuns.FindOneAndUpdateAsync(
            fb.Eq(x => x.Id, run.Id) &
            fb.Eq(x => x.CandidateChainId, binding.ChainId) &
            fb.Eq(x => x.StateRevision, run.StateRevision) &
            fb.Eq(x => x.StateHash, run.StateHash) &
            fb.In(x => x.Status, new[]
            {
                StatisticReconciliationRunStatuses.Queued,
                StatisticReconciliationRunStatuses.Running
            }) &
            fb.Gt(x => x.DeadlineAtUtc, now) &
            fb.Eq(x => x.IsDeleted, false),
            Builders<StatisticReconciliationRun>.Update
                .Set(x => x.Status, nextState.Status)
                .Set(x => x.StateRevision, nextState.StateRevision)
                .Set(x => x.StateHash, nextStateHash)
                .Set(x => x.NextRetryAtUtc, null)
                .Set(x => x.LeaseOwnerId, null)
                .Set(x => x.ClaimToken, null)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.LastHeartbeatAtUtc, null)
                .Set(x => x.DiagnosticCode, null)
                .Set(x => x.OperationReceiptHistoryHash, historyHash)
                .Push(x => x.OperationReceipts, receipt)
                .Set(x => x.CancelledAtUtc, now)
                .Set(x => x.LatestWriterUserId, actor.Id)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actor.Id),
            new FindOneAndUpdateOptions<StatisticReconciliationRun>
            {
                ReturnDocument = ReturnDocument.After
            },
            ct);
        if (updated is not null)
        {
            RequireReadIntegrity(updated);
            return ToSummary(updated);
        }

        var observed = await _ctx.StatisticReconciliationRuns
            .Find(x => x.Id == run.Id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw JobConflict("JOB_DISAPPEARED");
        RequireReadIntegrity(observed);
        observed = await RefreshTimeoutAsync(observed, actor.Id, ct);
        RequireReadIntegrity(observed);
        ThrowIfTimedOut(observed);
        replay = (observed.OperationReceipts ?? []).SingleOrDefault(item =>
            string.Equals(item.ActorUserId, actor.Id, StringComparison.Ordinal) &&
            string.Equals(item.Operation, "CANCEL", StringComparison.Ordinal) &&
            string.Equals(item.CommandId, commandId, StringComparison.Ordinal));
        if (replay is not null &&
            string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return ToSummary(observed, true);
        }
        throw RevisionConflict("STATE_CAS_LOST");
    }

    private StatisticReconciliationCreateResult ResolveCreateReplay(
        StatisticReconciliationRun existing,
        string requestHash,
        string requiredChainId,
        MeResponse actor)
    {
        if (!string.Equals(existing.ActorUserId, actor.Id, StringComparison.Ordinal) ||
            !string.Equals(existing.TenantUnitId, NormalizeTenantId(actor.UnitId), StringComparison.Ordinal) ||
            !string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            throw ReplayMismatch("CREATE_REQUEST_HASH_MISMATCH");
        }
        if (!string.Equals(existing.CandidateChainId, requiredChainId, StringComparison.Ordinal))
            throw ReplayMismatch("CREATE_CHAIN_MISMATCH");
        RequireReadIntegrity(existing);
        return new StatisticReconciliationCreateResult(true, ToSummary(existing, true));
    }

    private async Task<StatisticReconciliationRun>
        RestoreTimedOutRecheckBaseAsync(
            StatisticReconciliationRun run,
            string writerUserId,
            DateTime now,
            CancellationToken ct)
    {
        var marker = Recheck.StatisticReconciliationRecheckCanonical
            .Clone(run.Recheck!);
        Recheck.StatisticReconciliationRecheckCanonical
            .RequireValidMarker(marker);
        if (marker.Phase is not (
                StatisticReconciliationRecheckPhases.ReadyToClaim or
                StatisticReconciliationRecheckPhases.CaptureRunning))
            throw JobConflict("RECHECK_TIMEOUT_PHASE_INVALID");
        var failedAt = marker.BaseTerminalStatus ==
            StatisticReconciliationRunStatuses.Failed
                ? now
                : (DateTime?)null;
        var nextState = StateOf(run) with
        {
            Status = marker.BaseTerminalStatus,
            StateRevision = checked(run.StateRevision + 1),
            NextRetryAtUtc = null,
            LeaseOwnerId = null,
            ClaimToken = null,
            LeaseUntilUtc = null,
            LastHeartbeatAtUtc = null,
            DiagnosticCode = null,
            PendingGenerationId = null,
            PendingGenerationHash = null,
            PendingGenerationPublishedAtUtc = null,
            CurrentGenerationId = marker.BaseCurrentGenerationId,
            CurrentGenerationHash = marker.BaseCurrentGenerationHash,
            Recheck = null,
            FailedAtUtc = failedAt
        };
        var stateHash = BuildStateHash(run.Id, nextState);
        var fb = Builders<StatisticReconciliationRun>.Filter;
        var updated = await _ctx.StatisticReconciliationRuns
            .FindOneAndUpdateAsync(
                fb.Eq(value => value.Id, run.Id) &
                fb.Eq(value => value.StateRevision,
                    run.StateRevision) &
                fb.Eq(value => value.StateHash, run.StateHash) &
                fb.Eq(value => value.CurrentGenerationId,
                    marker.BaseCurrentGenerationId) &
                fb.Eq(value => value.CurrentGenerationHash,
                    marker.BaseCurrentGenerationHash) &
                fb.Eq("recheck.markerId", marker.MarkerId) &
                fb.Eq("recheck.markerStateHash",
                    marker.MarkerStateHash) &
                fb.Lte(value => value.DeadlineAtUtc, now) &
                fb.In(value => value.Status, new[]
                {
                    StatisticReconciliationRunStatuses.Queued,
                    StatisticReconciliationRunStatuses.Running
                }) &
                fb.Eq(value => value.IsDeleted, false),
                Builders<StatisticReconciliationRun>.Update
                    .Set(value => value.Status,
                        marker.BaseTerminalStatus)
                    .Set(value => value.StateRevision,
                        nextState.StateRevision)
                    .Set(value => value.StateHash, stateHash)
                    .Set(value => value.NextRetryAtUtc, null)
                    .Set(value => value.LeaseOwnerId, null)
                    .Set(value => value.ClaimToken, null)
                    .Set(value => value.LeaseUntilUtc, null)
                    .Set(value => value.LastHeartbeatAtUtc, null)
                    .Set(value => value.DiagnosticCode, null)
                    .Set(value => value.PendingGenerationId, null)
                    .Set(value => value.PendingGenerationHash, null)
                    .Set(value =>
                        value.PendingGenerationPublishedAtUtc, null)
                    .Set(value => value.Recheck, null)
                    .Set(value => value.FailedAtUtc, failedAt)
                    .Set(value => value.LatestWriterUserId,
                        writerUserId)
                    .Set(value => value.UpdatedAtUtc, now)
                    .Set(value => value.UpdatedByUserId,
                        writerUserId),
                new FindOneAndUpdateOptions<StatisticReconciliationRun>
                {
                    ReturnDocument = ReturnDocument.After
                },
                ct);
        if (updated is not null)
        {
            RequireReadIntegrity(updated);
            return updated;
        }
        var observed = await _ctx.StatisticReconciliationRuns
            .Find(value => value.Id == run.Id && !value.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (observed is null)
            return run;
        RequireReadIntegrity(observed);
        return observed;
    }
    private async Task<StatisticReconciliationRun> RefreshTimeoutAsync(
        StatisticReconciliationRun run,
        string writerUserId,
        CancellationToken ct)
    {
        var now = MongoUtcNow();
        if (run.DeadlineAtUtc > now ||
            run.Status is not (
                StatisticReconciliationRunStatuses.Queued or
                StatisticReconciliationRunStatuses.Running))
        {
            return run;
        }
        if (run.Recheck is not null)
            return await RestoreTimedOutRecheckBaseAsync(
                run, writerUserId, now, ct);
        var nextState = StateOf(run) with
        {
            Status = StatisticReconciliationRunStatuses.Failed,
            StateRevision = run.StateRevision + 1,
            NextRetryAtUtc = null,
            LeaseOwnerId = null,
            ClaimToken = null,
            LeaseUntilUtc = null,
            LastHeartbeatAtUtc = null,
            DiagnosticCode = "P10_RECONCILIATION_JOB_TIMEOUT",
            FailedAtUtc = now
        };
        var stateHash = BuildStateHash(run.Id, nextState);
        var fb = Builders<StatisticReconciliationRun>.Filter;
        var updated = await _ctx.StatisticReconciliationRuns.FindOneAndUpdateAsync(
            fb.Eq(x => x.Id, run.Id) &
            fb.Eq(x => x.StateRevision, run.StateRevision) &
            fb.Eq(x => x.StateHash, run.StateHash) &
            fb.Lte(x => x.DeadlineAtUtc, now) &
            fb.In(x => x.Status, new[]
            {
                StatisticReconciliationRunStatuses.Queued,
                StatisticReconciliationRunStatuses.Running
            }) &
            fb.Eq(x => x.IsDeleted, false),
            Builders<StatisticReconciliationRun>.Update
                .Set(x => x.Status, nextState.Status)
                .Set(x => x.StateRevision, nextState.StateRevision)
                .Set(x => x.StateHash, stateHash)
                .Set(x => x.NextRetryAtUtc, null)
                .Set(x => x.LeaseOwnerId, null)
                .Set(x => x.ClaimToken, null)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.LastHeartbeatAtUtc, null)
                .Set(x => x.DiagnosticCode, nextState.DiagnosticCode)
                .Set(x => x.FailedAtUtc, now)
                .Set(x => x.LatestWriterUserId, writerUserId)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, writerUserId),
            new FindOneAndUpdateOptions<StatisticReconciliationRun>
            {
                ReturnDocument = ReturnDocument.After
            },
            ct);
        if (updated is not null)
        {
            RequireReadIntegrity(updated);
            return updated;
        }
        var observed = await _ctx.StatisticReconciliationRuns
            .Find(x => x.Id == run.Id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (observed is null)
            return run;
        RequireReadIntegrity(observed);
        return observed;
    }
}
