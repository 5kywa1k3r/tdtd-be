using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Services.StatisticsRun;

public static class StatRunDirectProjectionStates
{
    public const string Published = "PUBLISHED";
    public const string ZeroWrite = "ZERO_WRITE";
}

public sealed record StatRunDirectProjectionResult(
    string State,
    string Reason,
    string? RunId,
    string? GenerationId,
    string? GenerationHash,
    bool IsReplay);

/// <summary>
/// Logical, server-owned pins copied from a claimed Foundation job. Random job
/// identity and client command/receipt fields are intentionally excluded: two
/// independently requested jobs for the same immutable inputs must converge on
/// one inner Direct publication.
/// </summary>
public sealed record StatRunFoundationRefreshPin(
    string CapabilityId,
    string RunKind,
    string WorkId,
    string ScopeType,
    string? ScopeId,
    string SourceReportId,
    int SourceRevision,
    string SourceHash,
    int LifecycleRevision,
    string DynamicFormTemplateId,
    string ConfigId,
    string ConfigVersionId,
    int ConfigVersionNo,
    long ConfigRevision,
    string ConfigHash,
    string CatalogVersion,
    string CatalogRawSha256,
    string CatalogSemanticSha256,
    string SchemaRawSha256,
    string SchemaSemanticSha256,
    string StageLockSha256,
    string CandidateChainId,
    string? FlowTemplateId,
    int? FlowFamilyRevision,
    int? FlowTemplateVersionNo,
    string? FlowTemplateVersionId,
    string? FlowPayloadHash,
    string? FlowCatalogVersion,
    string? FlowCatalogSemanticHash,
    string? FlowInstanceId,
    long? FlowInstanceRevision,
    string? FlowInstanceState,
    int? FlowExecutionEpoch,
    string? FlowExecutionEpochId,
    long? FlowExecutionEpochRevision,
    string? FlowExecutionEpochState,
    string? FlowBranchId,
    string? FlowStepId,
    int? FlowAttemptNo,
    string? FlowStepInstanceId,
    long? FlowStepInstanceRevision,
    string? FlowStepInstanceState,
    string PeriodKey,
    string PeriodInstanceKey,
    string PeriodKind,
    DateTime? PeriodStartUtc,
    DateTime? PeriodEndUtc);

internal sealed record StatRunDirectProjectionCanonicalIdentity(
    string Version,
    string Key,
    string RunId,
    string DedupeKey,
    string ReceiptId);

public interface IStatRunDirectProjectionService
{
    bool IsCandidateEnabled();

    Task<StatRunDirectProjectionResult> ProjectLifecycleEntryAsync(
        string reportId,
        string lifecycleEventKey,
        string actorUserId,
        CancellationToken ct = default);

    Task<StatRunDirectProjectionResult> ProjectFoundationRefreshAsync(
        StatRunFoundationRefreshPin pin,
        string lifecycleEventKey,
        string actorUserId,
        CancellationToken ct = default);

    Task<int> BackfillAcknowledgedAsync(
        int maxReports,
        CancellationToken ct = default);
}

/// <summary>
/// P9-02 approved/effective Direct projector. The existing six Direct collections remain
/// the only value ledger. A rebuild job owns one immutable full-scope generation and is
/// the sole publication point after all six store digests have been verified.
/// </summary>
public sealed partial class StatRunDirectProjectionService : IStatRunDirectProjectionService
{
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(10);
    private const int MaxRetryCount = 5;
    private const string WorkerPrefix = "p9-lfc";
    internal const string FoundationRefreshIdentityVersion =
        "FOUNDATION_REFRESH_V2";
    internal const string FoundationRefreshIdentitySchema =
        "P9_FOUNDATION_REFRESH_IDENTITY_V2";
    internal const string FoundationRefreshGenerationSchema =
        "P9_FOUNDATION_REFRESH_GENERATION_V2";
    internal const string FoundationRefreshHeaderSchema =
        "P9_FOUNDATION_REFRESH_HEADER_V2";
    internal const string FoundationRefreshRunNamespace =
        "P9_FOUNDATION_REFRESH_RUN_V2";
    internal const string FoundationRefreshReceiptNamespace =
        "P9_FOUNDATION_REFRESH_RECEIPT_V2";
    private const string FlowContributionOperationVersion =
        "P9_FLOW_CONTRIBUTION_V1";

    private readonly MongoDbContext _ctx;
    private readonly IStatRunCandidateActivation _activation;
    private readonly IWorkReportFieldStatisticsService _fieldStatistics;
    private readonly IWorkReportTableStatisticsService _tableStatistics;
    private readonly IWorkReportLabelStatisticsService _labelStatistics;
    private readonly IDynamicFlowDefinitionTransactionRunner _transactionRunner;
    private readonly ILogger<StatRunDirectProjectionService> _logger;

    public StatRunDirectProjectionService(
        MongoDbContext ctx,
        IStatRunCandidateActivation activation,
        IWorkReportFieldStatisticsService fieldStatistics,
        IWorkReportTableStatisticsService tableStatistics,
        IWorkReportLabelStatisticsService labelStatistics,
        IDynamicFlowDefinitionTransactionRunner transactionRunner,
        ILogger<StatRunDirectProjectionService> logger,
        IWorkReportPayloadReader? payloadReader = null)
    {
        _ctx = ctx;
        _activation = activation;
        _fieldStatistics = fieldStatistics;
        _tableStatistics = tableStatistics;
        _labelStatistics = labelStatistics;
        _transactionRunner = transactionRunner;
        _logger = logger;
        _nativePayloadReader = payloadReader;
    }

    public bool IsCandidateEnabled()
        => _activation.EvaluateFoundation(
            StatRunCapabilities.DirectFieldTableLabel,
            StatRunRouteRegistry.LifecycleDirectProjector).Enabled;

    internal static WorkReportDirectContributionBinding ResolveContributionBinding(
        WorkAssignmentReport report,
        string? flowInstanceId,
        string? lockedContributionPolicy)
    {
        ArgumentNullException.ThrowIfNull(report);
        var isLockedFlowPolicy = flowInstanceId is not null;
        return new WorkReportDirectContributionBinding(
            isLockedFlowPolicy
                ? lockedContributionPolicy ?? DynamicFlowContributionPolicyContract.Exclude
                : report.CumulativeContributionMode,
            isLockedFlowPolicy);
    }

    public Task<StatRunDirectProjectionResult> ProjectLifecycleEntryAsync(
        string reportId,
        string lifecycleEventKey,
        string actorUserId,
        CancellationToken ct = default)
        => ProjectAsync(reportId, lifecycleEventKey, actorUserId, null, ct);

    public Task<StatRunDirectProjectionResult> ProjectFoundationRefreshAsync(
        StatRunFoundationRefreshPin pin,
        string lifecycleEventKey,
        string actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return ProjectAsync(
            pin.SourceReportId,
            lifecycleEventKey,
            actorUserId,
            pin,
            ct);
    }

    private async Task<StatRunDirectProjectionResult> ProjectAsync(
        string reportId,
        string lifecycleEventKey,
        string actorUserId,
        StatRunFoundationRefreshPin? foundationPin,
        CancellationToken ct)
    {
        var binding = _activation.RequireFoundation(
            StatRunCapabilities.DirectFieldTableLabel,
            StatRunRouteRegistry.LifecycleDirectProjector);
        reportId = RequireObjectId(reportId, "SOURCE_REPORT_ID_INVALID");
        lifecycleEventKey = RequireHash(lifecycleEventKey, "LIFECYCLE_EVENT_KEY_INVALID");
        actorUserId = RequireObjectId(actorUserId, "ACTOR_USER_ID_INVALID");
        StatRunCandidateBinding? foundationRequestBinding = null;
        if (foundationPin is not null)
        {
            ValidateFoundationRefreshPinShape(foundationPin, reportId);
            foundationRequestBinding = _activation.RequireFoundation(
                foundationPin.CapabilityId,
                StatRunRouteRegistry.CoreJob(foundationPin.CapabilityId));
            if (!FoundationRefreshBindingContractMatches(
                    foundationPin,
                    foundationRequestBinding,
                    binding))
            {
                throw Fail("FOUNDATION_REFRESH_RESOLVED_PIN_STALE");
            }
        }

        var source = await LoadSourceAsync(reportId, lifecycleEventKey, ct);
        if (foundationPin is not null)
            ValidateFoundationRefreshSourcePin(foundationPin, source);
        var directSourceRevision = await LoadOrInitializeDirectSourceRevisionAsync(
            source.Report.WorkId,
            ct);
        source = await LoadSourceAsync(reportId, lifecycleEventKey, ct);
        var tenantUnitId = await ValidateTenantScopeAsync(source, actorUserId, ct);
        var template = await LoadPinnedTemplateAsync(source.Report, source.Assignment, ct);
        ValidateSourcePeriod(source.Report, source.Period);
        EnsureContributionPolicyWellFormed(source.Report);
        if (!ValidateProjectionStatisticConfig(template))
        {
            return await CompleteZeroWriteAsync(
                source.Report.Id,
                lifecycleEventKey,
                "STATISTICS_NOT_CONFIGURED",
                foundationPin is null,
                ct);
        }

        var members = await ResolveMembershipAsync(
            source.Report.WorkId,
            source.Report.PeriodInstanceKey,
            template,
            ct);
        var publicationScopeKey = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_DIRECT_PUBLICATION_SCOPE_V1",
            source.Report.WorkId,
            source.Report.PeriodInstanceKey,
            dynamicFormFamilyId = template.FamilyId,
            dynamicFormTemplateId = template.Id,
            dynamicFormVersionNo = template.VersionNo,
            dynamicFormSchemaHash = template.PublishedSchemaHash,
            configVersionId = template.StatisticConfigVersionId,
            configRevision = template.StatisticConfigRevision,
            configHash = template.StatisticConfigHash
        });
        var triggerMember = members.FirstOrDefault(item =>
            string.Equals(item.Report.Id, source.Report.Id, StringComparison.Ordinal));
        ReversalContext? reversal = null;
        if (triggerMember is null)
        {
            reversal = await ResolveReversalContextAsync(
                source,
                publicationScopeKey,
                ct);
            if (reversal is null)
            {
                return await CompleteZeroWriteAsync(
                    source.Report.Id,
                    lifecycleEventKey,
                    "SOURCE_NOT_EFFECTIVE",
                    foundationPin is null,
                    ct);
            }
            triggerMember = reversal.TriggerMember;
            if (!reversal.IsReplay)
            {
                await MarkPriorPublicationStaleAsync(
                    reversal.PriorPublication,
                    actorUserId,
                    ct);
            }
        }

        var membershipSignature = BuildProjectionMembershipSignature(members, template);
        if (foundationPin is not null)
        {
            if (reversal is not null)
                throw Fail("FOUNDATION_REFRESH_REVERSAL_INVALID");
            ValidateFoundationRefreshResolvedPin(
                foundationPin,
                source,
                triggerMember,
                template);
        }

        var legacyGenerationId = reversal is null
            ? StatRunCanonicalJson.HashObject(new
            {
                version = "P9_DIRECT_GENERATION_V1",
                binding.ChainId,
                binding.PromptId,
                sourceReportId = source.Report.Id,
                lifecycleEventKey,
                publicationScopeKey,
                membershipSignature
            })
            : StatRunCanonicalJson.HashObject(new
            {
                version = "P9_DIRECT_REVERSAL_GENERATION_V1",
                binding.ChainId,
                binding.PromptId,
                sourceReportId = source.Report.Id,
                lifecycleEventKey,
                publicationScopeKey,
                membershipSignature,
                reversalOfRunId = reversal.PriorPublication.Id,
                reversalOfGenerationId = reversal.PriorPublication.GenerationId,
                reversalOfLedgerHash = reversal.PriorPublication.FlowContributionLedgerHash
            });
        var identity = await ResolveProjectionIdentityAsync(
            source,
            publicationScopeKey,
            membershipSignature,
            legacyGenerationId,
            foundationPin is not null,
            ct);
        var generationId = identity.IsFoundationRefresh
            ? BuildFoundationRefreshGenerationId(
                identity.Key!,
                binding.ChainId,
                binding.PromptId,
                source.Report.Id,
                lifecycleEventKey,
                publicationScopeKey,
                membershipSignature)
            : legacyGenerationId;
        var runId = identity.RunId;
        var computedAtUtc = ResolveComputedAtUtc(source.Entry);
        var context = new WorkReportDirectGenerationContext(
            runId,
            generationId,
            lifecycleEventKey,
            directSourceRevision,
            template.FamilyId!,
            template.Id,
            template.VersionNo,
            template.PublishedSchemaHash!,
            template.StatisticConfigId!,
            template.StatisticConfigVersionId!,
            template.StatisticConfigVersionNo,
            template.StatisticConfigRevision,
            template.StatisticConfigHash!,
            binding.ChainId,
            binding.CatalogVersion,
            binding.CatalogRawSha256,
            binding.CatalogSemanticSha256,
            binding.SchemaRawSha256,
            binding.SchemaSemanticSha256,
            binding.StageLockSha256,
            membershipSignature,
            members.ToDictionary(
                member => member.Report.Id,
                member => ResolveContributionBinding(
                    member.Report,
                    member.Runtime.FlowInstanceId,
                    member.Runtime.LockedContributionPolicy),
                StringComparer.Ordinal),
            computedAtUtc);

        var claim = await AcquireRunAsync(
            source,
            triggerMember,
            template,
            binding,
            context,
            identity,
            publicationScopeKey,
            members.Count,
            actorUserId,
            tenantUnitId,
            members,
            reversal,
            ct);
        if (claim.Completed is not null)
        {
            if (identity.LinkLifecycleEntry)
                await LinkPublishedAsync(source.Report.Id, lifecycleEventKey, claim.Completed, ct);
            return new StatRunDirectProjectionResult(
                StatRunDirectProjectionStates.Published,
                "EXACT_REPLAY",
                claim.Completed.Id,
                claim.Completed.GenerationId,
                claim.Completed.GenerationHash,
                true);
        }
        if (!string.IsNullOrWhiteSpace(claim.TerminalReason))
        {
            return await CompleteZeroWriteAsync(
                source.Report.Id,
                lifecycleEventKey,
                claim.TerminalReason,
                identity.LinkLifecycleEntry,
                ct);
        }

        try
        {
            var native = template.NativeTablesVersion is null ? null
                : await StageNativeProjectionAsync(source, template, binding, context,
                    actorUserId, reversal is null, ct);
            var memberReportIds = members
                .Select(member => member.Report.Id)
                .OrderBy(reportId => reportId, StringComparer.Ordinal)
                .ToArray();
            await _fieldStatistics.StageGenerationValuesForReportsAsync(
                memberReportIds, context, actorUserId, ct);
            await _tableStatistics.StageGenerationValuesForReportsAsync(
                memberReportIds, context, actorUserId, ct);
            await _labelStatistics.StageGenerationValuesForReportsAsync(
                memberReportIds, context, actorUserId, ct);

            await _fieldStatistics.StageGenerationAggregatesForWorkPeriodAsync(
                source.Report.WorkId, source.Report.PeriodInstanceKey, template.Id,
                context, actorUserId, ct);
            await _tableStatistics.StageGenerationAggregatesForWorkPeriodAsync(
                source.Report.WorkId, source.Report.PeriodInstanceKey, template.Id,
                context, actorUserId, ct);
            await _labelStatistics.StageGenerationAggregatesForWorkPeriodAsync(
                source.Report.WorkId, source.Report.PeriodInstanceKey, template.Id,
                context, actorUserId, ct);

            var digests = await ComputeAndValidateDigestsAsync(context, members, ct);
            var contributionAudit = await BuildFlowContributionAuditAsync(
                context,
                members,
                claim.Job!.ReceiptId ?? throw Fail("RUN_RECEIPT_MISSING"),
                ct);
            var reversalAudit = BuildReversalAudit(
                context,
                source,
                claim.Job.ReceiptId!,
                reversal);
            var orderedDigests = digests
                .OrderBy(item => item.Store, StringComparer.Ordinal)
                .Select(item => new { item.Store, item.RowCount, item.Sha256 })
                .ToArray();
            var generationHash = reversalAudit is null
                ? StatRunCanonicalJson.HashObject(new
                {
                    version = "P9_DIRECT_GENERATION_HASH_V3",
                    context.GenerationId,
                    context.SourceMembershipSignature,
                    contributionAudit.LedgerHash,
                    contributionAudit.ReversalBaselineHash,
                    stores = orderedDigests
                })
                : StatRunCanonicalJson.HashObject(new
                {
                    version = "P9_DIRECT_REVERSAL_GENERATION_HASH_V2",
                    context.GenerationId,
                    context.SourceMembershipSignature,
                    contributionAudit.LedgerHash,
                    contributionAudit.ReversalBaselineHash,
                    reversalAuditHash = reversalAudit.AuditHash,
                    stores = orderedDigests
                });

            generationHash = NativeStatisticPublicationContract.BindHash(generationHash, native?.Publication);
            await RevalidateBeforePublishAsync(
                source, template, binding, context, membershipSignature,
                reversal is null, ct);
            var published = await PublishAsync(
                claim.Job!, claim.WorkerId!, claim.ClaimToken!, source, context,
                identity, generationHash, digests, contributionAudit, reversalAudit, native,
                members.Count,
                actorUserId, ct);
            if (identity.LinkLifecycleEntry)
                await LinkPublishedAsync(source.Report.Id, lifecycleEventKey, published, ct);
            return new StatRunDirectProjectionResult(
                StatRunDirectProjectionStates.Published,
                "PUBLISHED",
                published.Id,
                published.GenerationId,
                published.GenerationHash,
                false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ReleaseForRetryAsync(
                claim.Job!, claim.WorkerId!, claim.ClaimToken!,
                "CANCELLED", CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (
            ShouldTerminalizeClaimedProjectionFailure(
                foundationPin is not null,
                exception))
        {
            var failure = StatRunFoundationDiagnostics.ClassifyProjectionFailure(exception);
            WorkReportStatisticRebuildJob? completed = null;
            try
            {
                completed = await TerminalizeStaleRunAsync(
                    claim.Job!,
                    failure.DiagnosticCode,
                    source.Report.Id,
                    lifecycleEventKey,
                    CancellationToken.None);
            }
            catch (Exception terminalizationError)
            {
                var (category, code) =
                    StatRunFoundationDiagnostics.Describe(terminalizationError);
                _logger.LogWarning(
                    "Could not terminalize claimed Foundation Direct run. runId={RunId} category={Category} code={Code}",
                    claim.Job!.Id,
                    category,
                    code);
            }
            if (completed is not null)
            {
                return new StatRunDirectProjectionResult(
                    StatRunDirectProjectionStates.Published,
                    "CONCURRENT_PUBLISHED",
                    completed.Id,
                    completed.GenerationId,
                    completed.GenerationHash,
                    true);
            }
            throw;
        }
        catch (Exception exception) when (IsTerminalStaleFailure(exception))
        {
            var reason = StableDiagnostic(exception);
            var completed = await TerminalizeStaleRunAsync(
                claim.Job!,
                reason,
                source.Report.Id,
                lifecycleEventKey,
                CancellationToken.None);
            if (completed is not null)
            {
                if (identity.LinkLifecycleEntry)
                {
                    await LinkPublishedAsync(
                        source.Report.Id,
                        lifecycleEventKey,
                        completed,
                        CancellationToken.None);
                }
                return new StatRunDirectProjectionResult(
                    StatRunDirectProjectionStates.Published,
                    "CONCURRENT_PUBLISHED",
                    completed.Id,
                    completed.GenerationId,
                    completed.GenerationHash,
                    true);
            }
            return await CompleteZeroWriteAsync(
                source.Report.Id,
                lifecycleEventKey,
                reason,
                identity.LinkLifecycleEntry,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            await ReleaseForRetryAsync(
                claim.Job!, claim.WorkerId!, claim.ClaimToken!,
                StableDiagnostic(exception), CancellationToken.None);
            throw;
        }
    }

    public async Task<int> BackfillAcknowledgedAsync(
        int maxReports,
        CancellationToken ct = default)
    {
        if (!IsCandidateEnabled())
            return 0;

        maxReports = Math.Clamp(maxReports, 1, 200);
        var ef = Builders<WorkReportLifecycleProjectionOutboxEntry>.Filter;
        var eligibleEntry = ef.Eq(x => x.State, WorkReportLifecycleProjectionOutboxStates.Completed) &
                             ef.In(x => x.Operation, new[]
                             {
                                 "REVIEW_APPROVE",
                                 "REVIEW_CONFIRM_AUTO_APPROVE",
                                 "REVIEW_RECALL_APPROVED",
                                 "REVIEW_RETURN",
                                 "REVIEW_DEACTIVATE_REPORT",
                                 "REVIEW_REACTIVATE_REPORT",
                                 "WITHDRAW",
                                 "AUTO_AGGREGATE_REVIEW_INVALIDATED"
                             }) &
                             (ef.Exists(x => x.DirectProjectionState, false) |
                              ef.Eq(x => x.DirectProjectionState, null));
        var rf = Builders<WorkAssignmentReport>.Filter;
        var reports = await _ctx.WorkAssignmentReports
            .Find(rf.Eq(x => x.IsDeleted, false) &
                  rf.ElemMatch(x => x.LifecycleProjectionOutbox, eligibleEntry))
            .SortBy(x => x.UpdatedAtUtc)
            .ThenBy(x => x.Id)
            .Limit(maxReports)
            .ToListAsync(ct);

        var processed = 0;
        foreach (var report in reports)
        {
            var candidates = (report.LifecycleProjectionOutbox ??
                              new List<WorkReportLifecycleProjectionOutboxEntry>())
                .Where(item =>
                    string.Equals(item.State, WorkReportLifecycleProjectionOutboxStates.Completed, StringComparison.Ordinal) &&
                    (IsInitialApprovalEntry(item) ||
                     IsReactivationEntry(item) ||
                     IsNonEffectiveLifecycleEntry(item)) &&
                    string.IsNullOrWhiteSpace(item.DirectProjectionState))
                .OrderByDescending(item => item.CreatedAtUtc)
                .ThenByDescending(item => item.EntryKey, StringComparer.Ordinal)
                .ToList();
            if (candidates.Count == 0)
                continue;

            var reportProcessed = false;
            var currentEntries = candidates
                .Where(item =>
                    item.LifecycleRevision == report.LifecycleRevision &&
                    item.PayloadRevision == report.PayloadRevision &&
                    string.Equals(item.PayloadHash, report.PayloadHash, StringComparison.Ordinal))
                .ToList();
            var currentEntry = currentEntries.Count == 1 ? currentEntries[0] : null;

            foreach (var stale in candidates.Where(item => !ReferenceEquals(item, currentEntry)))
            {
                try
                {
                    await LinkZeroWriteAsync(
                        report.Id,
                        stale.EntryKey,
                        "SUPERSEDED_LIFECYCLE_ENTRY",
                        ct);
                    reportProcessed = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "P9 Direct historical lifecycle entry terminalization deferred. reportId={ReportId} entryKey={EntryKey}",
                        report.Id,
                        stale.EntryKey);
                }
            }

            if (currentEntry is null)
            {
                if (reportProcessed)
                    processed++;
                continue;
            }

            try
            {
                await ProjectLifecycleEntryAsync(
                    report.Id, currentEntry.EntryKey, ResolveActor(report, currentEntry), ct);
                reportProcessed = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (IsPermanentBackfillFailure(exception))
                {
                    try
                    {
                        await LinkZeroWriteAsync(
                            report.Id,
                            currentEntry.EntryKey,
                            StableDiagnostic(exception),
                            ct);
                        reportProcessed = true;
                    }
                    catch (Exception terminalException)
                    {
                        _logger.LogWarning(
                            terminalException,
                            "P9 Direct activation backfill terminal link failed. reportId={ReportId}",
                            report.Id);
                    }
                }
                _logger.LogWarning(
                    "P9 Direct activation backfill deferred. reportId={ReportId} reason={Reason}",
                    report.Id,
                    StableDiagnostic(exception));
            }
            if (reportProcessed)
                processed++;
        }

        return processed;
    }

    private async Task<DirectSource> LoadSourceAsync(
        string reportId,
        string lifecycleEventKey,
        CancellationToken ct)
    {
        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw Fail("SOURCE_REPORT_MISSING");
        var entry = (report.LifecycleProjectionOutbox ??
                     new List<WorkReportLifecycleProjectionOutboxEntry>())
            .SingleOrDefault(item =>
                string.Equals(item.EntryKey, lifecycleEventKey, StringComparison.Ordinal))
            ?? throw Fail("LIFECYCLE_EVENT_MISSING");
        ValidateLifecycleEntry(report, entry);

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw Fail("SOURCE_ASSIGNMENT_MISSING");
        if (!string.Equals(assignment.WorkId, report.WorkId, StringComparison.Ordinal))
            throw Fail("SOURCE_WORK_BINDING_INVALID");

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw Fail("SOURCE_PERIOD_MISSING");
        return new DirectSource(report, entry, assignment, period);
    }

    private async Task<long> LoadOrInitializeDirectSourceRevisionAsync(
        string workId,
        CancellationToken ct)
    {
        workId = RequireObjectId(workId, "SOURCE_WORK_ID_INVALID");
        var work = await _ctx.Works
            .Find(x => x.Id == workId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw Fail("SOURCE_WORK_MISSING");
        if (work.DirectSourceRevision > 0)
            return work.DirectSourceRevision;

        var fb = Builders<Work>.Filter;
        var initialized = await _ctx.Works.FindOneAndUpdateAsync(
            fb.Eq(x => x.Id, workId) &
            fb.Eq(x => x.IsDeleted, false) &
            (fb.Eq(x => x.DirectSourceRevision, 0) |
             fb.Exists(x => x.DirectSourceRevision, false)),
            Builders<Work>.Update.Set(x => x.DirectSourceRevision, 1),
            new FindOneAndUpdateOptions<Work>
            {
                ReturnDocument = ReturnDocument.After
            },
            ct);
        if (initialized is not null)
            return initialized.DirectSourceRevision;

        work = await _ctx.Works
            .Find(x => x.Id == workId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw Fail("SOURCE_WORK_MISSING");
        return work.DirectSourceRevision > 0
            ? work.DirectSourceRevision
            : throw Fail("SOURCE_REVISION_INITIALIZATION_LOST");
    }

    private async Task<string> ValidateTenantScopeAsync(
        DirectSource source,
        string actorUserId,
        CancellationToken ct)
    {
        if (!string.Equals(source.Entry.ActorUserId, actorUserId, StringComparison.Ordinal))
        {
            throw Fail("ACTOR_SCOPE_INVALID");
        }
        if (IsInitialApprovalEntry(source.Entry) &&
            (!string.Equals(source.Report.ApprovedByUserId, actorUserId, StringComparison.Ordinal) ||
             !string.Equals(source.Assignment.CreatedByUserId, actorUserId, StringComparison.Ordinal)))
        {
            throw Fail("APPROVAL_ACTOR_SCOPE_INVALID");
        }

        var actor = await _ctx.Users
            .Find(x => x.Id == actorUserId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw Fail("ACTOR_MISSING");
        var actorUnitId = RequireObjectId(actor.UnitId, "ACTOR_UNIT_INVALID");
        var targetUnitIds = source.Assignment.TargetUnitIds ??
                            new List<string>();
        if (!string.Equals(
                source.Assignment.IssuedByUnitId,
                actorUnitId,
                StringComparison.Ordinal) &&
            !targetUnitIds.Contains(actorUnitId, StringComparer.Ordinal))
        {
            throw Fail("TENANT_SCOPE_INVALID");
        }

        var assignee = (source.Assignment.Assignees ?? new List<UserRef>())
            .SingleOrDefault(item =>
                string.Equals(
                    item.UserId,
                    source.Report.AssigneeUserId,
                    StringComparison.Ordinal));
        var assigneeUnitId = RequireObjectId(
            assignee?.UnitId,
            "ASSIGNEE_UNIT_INVALID");
        if (!string.Equals(
                source.Period.AssigneeUserId,
                source.Report.AssigneeUserId,
                StringComparison.Ordinal) ||
            !string.Equals(
                source.Period.AssigneeUnitId,
                assigneeUnitId,
                StringComparison.Ordinal) ||
            !targetUnitIds.Contains(assigneeUnitId, StringComparer.Ordinal))
        {
            throw Fail("ASSIGNEE_SCOPE_INVALID");
        }

        return actorUnitId;
    }

    private static void ValidateLifecycleEntry(
        WorkAssignmentReport report,
        WorkReportLifecycleProjectionOutboxEntry entry)
    {
        var computed = WorkReportLifecycleOutboxContract.ComputeEntryKey(
            entry.CommandId,
            entry.LifecycleRevision,
            entry.Operation);
        var isEffective = IsEffectiveSource(report);
        var supportedTransition = IsInitialApprovalEntry(entry) ||
                                  IsReactivationEntry(entry) ||
                                  IsNonEffectiveLifecycleEntry(entry);
        if (!string.Equals(entry.EntryKey, computed, StringComparison.Ordinal) ||
            !supportedTransition ||
            (entry.State is not (
                WorkReportLifecycleProjectionOutboxStates.Pending or
                WorkReportLifecycleProjectionOutboxStates.Completed)) ||
            entry.LifecycleRevision != report.LifecycleRevision ||
            entry.PayloadRevision != report.PayloadRevision ||
            !string.Equals(entry.PayloadHash, report.PayloadHash, StringComparison.Ordinal) ||
            !string.Equals(entry.ToStatus, report.Status.ToString(), StringComparison.OrdinalIgnoreCase) ||
            entry.ToIsActive != report.IsActive ||
            (isEffective && !(IsInitialApprovalEntry(entry) || IsReactivationEntry(entry))) ||
            (!isEffective && !IsNonEffectiveLifecycleEntry(entry)) ||
            !StatRunCanonicalJson.IsCanonicalSha256(report.PayloadHash) ||
            !string.Equals(report.LastLifecycleCommandId, entry.CommandId, StringComparison.Ordinal) ||
            !string.Equals(report.LastLifecycleCommandOperation, entry.Operation, StringComparison.Ordinal) ||
            !StatRunCanonicalJson.IsCanonicalSha256(report.LastLifecycleCommandHash) ||
            report.LastLifecycleCommandRevision != entry.LifecycleRevision ||
            report.LastLifecycleCommandPayloadRevision != entry.PayloadRevision ||
            report.LastLifecycleCommandStatus != report.Status ||
            report.LastLifecycleCommandIsActive != report.IsActive)
        {
            throw Fail("LIFECYCLE_SOURCE_CONFLICT");
        }
    }

    private static bool IsEffectiveSource(WorkAssignmentReport report)
        => report.Status == WorkAssignmentReportStatus.Approved &&
           report.IsCurrent &&
           report.IsActive &&
           !report.IsDeleted &&
           string.IsNullOrWhiteSpace(report.InvalidatedByFlowEventId);

    private static bool IsInitialApprovalEntry(WorkReportLifecycleProjectionOutboxEntry entry)
        => (entry.Operation is "REVIEW_APPROVE" or "REVIEW_CONFIRM_AUTO_APPROVE") &&
           string.Equals(entry.FromStatus, "SUBMITTED", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(entry.ToStatus, "APPROVED", StringComparison.OrdinalIgnoreCase) &&
           entry.ToIsActive;

    private static bool IsReactivationEntry(WorkReportLifecycleProjectionOutboxEntry entry)
        => string.Equals(entry.Operation, "REVIEW_REACTIVATE_REPORT", StringComparison.Ordinal) &&
           string.Equals(entry.ToStatus, "APPROVED", StringComparison.OrdinalIgnoreCase) &&
           !entry.FromIsActive &&
           entry.ToIsActive;

    private static bool IsNonEffectiveLifecycleEntry(
        WorkReportLifecycleProjectionOutboxEntry entry)
        => (entry.Operation is
               "REVIEW_RECALL_APPROVED" or
               "REVIEW_RETURN" or
               "REVIEW_DEACTIVATE_REPORT" or
               "WITHDRAW" or
               "AUTO_AGGREGATE_REVIEW_INVALIDATED" or
               "REVIEW_REACTIVATE_REPORT") &&
           (!string.Equals(entry.ToStatus, "APPROVED", StringComparison.OrdinalIgnoreCase) ||
            !entry.ToIsActive);

    private async Task<DynamicFormTemplate> LoadPinnedTemplateAsync(
        WorkAssignmentReport report,
        WorkAssignment assignment,
        CancellationToken ct)
    {
        var templateId = report.DynamicFormTemplateId ?? assignment.DynamicFormTemplateId;
        if (!ObjectId.TryParse(templateId, out _))
            throw Fail("CONFIG_OWNER_INVALID");
        var template = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == templateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw Fail("CONFIG_OWNER_MISSING");
        if (!template.IsActive ||
            !template.IsPublished ||
            !string.Equals(report.DynamicFormTemplateId, template.Id, StringComparison.Ordinal) ||
            !string.Equals(assignment.DynamicFormTemplateId, template.Id, StringComparison.Ordinal) ||
            !string.Equals(report.DynamicFormFamilyId, template.FamilyId, StringComparison.Ordinal) ||
            !string.Equals(assignment.DynamicFormFamilyId, template.FamilyId, StringComparison.Ordinal) ||
            report.DynamicFormVersionNo != template.VersionNo ||
            assignment.DynamicFormVersionNo != template.VersionNo ||
            !StatRunCanonicalJson.IsCanonicalSha256(template.PublishedSchemaHash) ||
            !string.Equals(
                report.DynamicFormSchemaHash,
                template.PublishedSchemaHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                assignment.DynamicFormSchemaHash,
                template.PublishedSchemaHash,
                StringComparison.Ordinal))
        {
            throw Fail("FORM_SOURCE_PIN_INVALID");
        }
        return template;
    }

    private async Task<DynamicFormTemplate> LoadLockedTemplateAsync(
        WorkAssignmentReport report,
        WorkAssignment assignment,
        CancellationToken ct)
    {
        var template = await LoadPinnedTemplateAsync(report, assignment, ct);
        if (!ValidateProjectionStatisticConfig(template))
            throw Fail("LOCKED_CONFIG_MISSING");
        return template;
    }

    private static void ValidateLockedStatisticConfig(
        DynamicFormTemplate template,
        DynamicFormStatisticConfigCommandService.P804TrustedPersistedView trusted)
    {
        if (!string.Equals(template.StatisticConfigStatus, "LOCKED", StringComparison.Ordinal) ||
            !ObjectId.TryParse(template.StatisticConfigId, out _) ||
            !ObjectId.TryParse(template.StatisticConfigVersionId, out _) ||
            template.StatisticConfigVersionNo < 1 ||
            template.StatisticConfigRevision < 1 ||
            !StatRunCanonicalJson.IsCanonicalSha256(template.StatisticConfigHash) ||
            !string.Equals(trusted.Status, "LOCKED", StringComparison.Ordinal) ||
            !string.Equals(trusted.ConfigId, template.StatisticConfigId, StringComparison.Ordinal) ||
            !string.Equals(trusted.VersionId, template.StatisticConfigVersionId, StringComparison.Ordinal) ||
            trusted.VersionNo != template.StatisticConfigVersionNo ||
            trusted.Revision != template.StatisticConfigRevision ||
            !string.Equals(trusted.ConfigHash, template.StatisticConfigHash, StringComparison.Ordinal))
        {
            throw Fail("LOCKED_CONFIG_INVALID");
        }
    }

    private static void ValidatePeriod(
        WorkAssignmentReport report,
        WorkReportPeriod period)
    {
        if (!period.IsActive ||
            !string.Equals(period.WorkId, report.WorkId, StringComparison.Ordinal) ||
            !string.Equals(period.WorkAssignmentId, report.WorkAssignmentId, StringComparison.Ordinal) ||
            !string.Equals(period.CurrentReportId, report.Id, StringComparison.Ordinal) ||
            !string.Equals(period.PeriodKey, report.PeriodKey, StringComparison.Ordinal) ||
            !string.Equals(period.PeriodInstanceKey, report.PeriodInstanceKey, StringComparison.Ordinal) ||
            !string.Equals(period.PeriodKind, report.PeriodKind, StringComparison.Ordinal) ||
            !string.Equals(period.SourceLifecycleReportId, report.Id, StringComparison.Ordinal) ||
            period.SourceLifecycleRevision != report.LifecycleRevision ||
            !period.SourceLifecycleAppliedAtUtc.HasValue ||
            period.Status is not (WorkReportPeriodStatus.Approved or WorkReportPeriodStatus.OverdueApproved) ||
            string.IsNullOrWhiteSpace(report.PeriodInstanceKey) ||
            (report.PeriodStart.HasValue &&
             report.PeriodEnd.HasValue &&
             report.PeriodStart.Value > report.PeriodEnd.Value))
        {
            throw Fail("PERIOD_PIN_INVALID");
        }
    }

    private static void ValidateSourcePeriod(
        WorkAssignmentReport report,
        WorkReportPeriod period)
    {
        if (IsEffectiveSource(report))
        {
            ValidatePeriod(report, period);
            return;
        }

        if (!period.IsActive ||
            !string.Equals(period.WorkId, report.WorkId, StringComparison.Ordinal) ||
            !string.Equals(period.WorkAssignmentId, report.WorkAssignmentId, StringComparison.Ordinal) ||
            !string.Equals(period.PeriodKey, report.PeriodKey, StringComparison.Ordinal) ||
            !string.Equals(period.PeriodInstanceKey, report.PeriodInstanceKey, StringComparison.Ordinal) ||
            !string.Equals(period.PeriodKind, report.PeriodKind, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(report.PeriodInstanceKey) ||
            (report.PeriodStart.HasValue &&
             report.PeriodEnd.HasValue &&
             report.PeriodStart.Value > report.PeriodEnd.Value))
        {
            throw Fail("PERIOD_SOURCE_BINDING_INVALID");
        }
    }

    private async Task<List<DirectMember>> ResolveMembershipAsync(
        string workId,
        string periodInstanceKey,
        DynamicFormTemplate template,
        CancellationToken ct)
    {
        var reports = await _ctx.WorkAssignmentReports
            .Find(x => x.WorkId == workId &&
                       x.PeriodInstanceKey == periodInstanceKey &&
                       x.DynamicFormTemplateId == template.Id &&
                       x.Status == WorkAssignmentReportStatus.Approved &&
                       x.IsCurrent &&
                       x.IsActive &&
                       x.InvalidatedByFlowEventId == null &&
                       !x.IsDeleted)
            .SortBy(x => x.Id)
            .ToListAsync(ct);
        var members = new List<DirectMember>(reports.Count);
        foreach (var report in reports)
        {
            EnsureContributionPolicyWellFormed(report);
            var approvalEntry = RequireCanonicalApprovalEntry(report);
            var assignment = await _ctx.WorkAssignments
                .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (assignment is null ||
                !assignment.IsActive ||
                !string.IsNullOrWhiteSpace(assignment.InvalidatedByFlowEventId) ||
                !string.Equals(assignment.WorkId, report.WorkId, StringComparison.Ordinal))
            {
                continue;
            }
            var period = await _ctx.WorkReportPeriods
                .Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw Fail("MEMBER_PERIOD_MISSING");
            ValidatePeriod(report, period);
            if (!string.Equals(report.DynamicFormFamilyId, template.FamilyId, StringComparison.Ordinal) ||
                report.DynamicFormVersionNo != template.VersionNo ||
                !string.Equals(
                    report.DynamicFormSchemaHash,
                    template.PublishedSchemaHash,
                    StringComparison.Ordinal) ||
                !string.Equals(assignment.DynamicFormTemplateId, template.Id, StringComparison.Ordinal) ||
                !string.Equals(assignment.DynamicFormFamilyId, template.FamilyId, StringComparison.Ordinal) ||
                assignment.DynamicFormVersionNo != template.VersionNo ||
                !string.Equals(
                    assignment.DynamicFormSchemaHash,
                    template.PublishedSchemaHash,
                    StringComparison.Ordinal))
            {
                throw Fail("MEMBER_FORM_PIN_INVALID");
            }
            var tenant = await ResolveMemberTenantPinAsync(
                report,
                assignment,
                period,
                approvalEntry,
                ct);
            var runtime = await ResolveEffectiveRuntimeAsync(report, assignment, ct);
            if (runtime is null)
                continue;
            var reportIncludes = WorkReportCumulativeContributionPolicy
                .FromReport(report)
                .IncludesReport;
            if (!string.IsNullOrWhiteSpace(runtime.LockedContributionPolicy))
            {
                reportIncludes = string.Equals(
                    runtime.LockedContributionPolicy,
                    DynamicFlowContributionPolicyContract.Include,
                    StringComparison.Ordinal);
            }
            if (!reportIncludes)
                continue;
            var mapping = await DynamicFlowMappingLifecycleContract.ValidateAsync(
                _ctx,
                report,
                DynamicFlowMappingIntegrityMode.RequireCurrent,
                ct,
                lifecycleOperation: "P9_DIRECT_MEMBERSHIP");
            if (runtime.FlowInstanceId is not null &&
                runtime.FlowContributionOriginVersionId is not null &&
                string.Equals(
                    runtime.LockedContributionPolicy,
                    DynamicFlowContributionPolicyContract.Include,
                    StringComparison.Ordinal) &&
                mapping is null)
            {
                LogRuntimeAdmissionRejected(report.Id, "FLOW_MAPPING_LINEAGE_MISSING");
                continue;
            }
            members.Add(new DirectMember(
                report,
                assignment,
                period,
                approvalEntry,
                tenant,
                mapping,
                runtime));
        }
        return members;
    }

    private async Task<ReversalContext?> ResolveReversalContextAsync(
        DirectSource source,
        string publicationScopeKey,
        CancellationToken ct)
    {
        if (IsEffectiveSource(source.Report) ||
            !IsNonEffectiveLifecycleEntry(source.Entry))
        {
            return null;
        }

        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var prior = await _ctx.WorkReportStatisticRebuildJobs
            .Find(fb.Eq(x => x.PublicationScopeKey, publicationScopeKey) &
                  fb.Eq(x => x.RunKind, WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection) &
                  fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.Completed) &
                  fb.Eq(x => x.IsCurrentPublication, true) &
                  fb.Eq(x => x.IsDeleted, false))
            .SortByDescending(x => x.DirectPublicationRevision)
            .Limit(2)
            .ToListAsync(ct);
        if (prior.Count > 1)
            throw Fail("CURRENT_PUBLICATION_AMBIGUOUS");
        if (prior.Count == 0)
            return null;

        var publication = prior[0];
        await ValidateCompletedPublicationAsync(publication, requireCurrent: true, ct);
        var currentReversalAudit = publication.ReversalAudit;
        if (currentReversalAudit is not null &&
            string.Equals(
                currentReversalAudit.EventKey,
                source.Entry.EntryKey,
                StringComparison.Ordinal) &&
            ObjectId.TryParse(currentReversalAudit.PriorRunId, out _))
        {
            var replayPrior = await _ctx.WorkReportStatisticRebuildJobs
                .Find(x => x.Id == currentReversalAudit.PriorRunId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw Fail("REVERSAL_PRIOR_PUBLICATION_MISSING");
            await ValidateCompletedPublicationAsync(replayPrior, requireCurrent: false, ct);
            publication = replayPrior;
        }
        var sourceLedger = (publication.FlowContributionSources ?? [])
            .SingleOrDefault(item => string.Equals(
                item.SourceReportId,
                source.Report.Id,
                StringComparison.Ordinal));
        var sourceLedgerRevision = FindContributionSourceLifecycleRevision(
            publication,
            source.Report.Id);
        if (publication.NativeStatisticPublication is not null)
        {
            var artifact = await NativeStatisticPublicationContract.ReadAsync(_ctx.Db, publication, ct);
            var nativeRevision = artifact.Sources.SingleOrDefault(item => item.ReportId == source.Report.Id)?.LifecycleRevision;
            if (sourceLedgerRevision is not null && nativeRevision is not null && sourceLedgerRevision != nativeRevision)
                throw Fail("NATIVE_REVERSAL_LEDGER_MISMATCH");
            sourceLedgerRevision ??= nativeRevision;
        }
        var pinnedRevisions = await LoadDirectSourceLifecycleRevisionsAsync(
            publication.GenerationId!,
            source.Report.Id,
            ct);
        if (sourceLedgerRevision is not null &&
            sourceLedgerRevision.Value >= source.Report.LifecycleRevision)
        {
            return null;
        }
        if (pinnedRevisions.Count > 1 ||
            pinnedRevisions.Any(revision => revision >= source.Report.LifecycleRevision) ||
            (sourceLedgerRevision is not null &&
             pinnedRevisions.Count == 1 &&
             pinnedRevisions[0] != sourceLedgerRevision.Value))
        {
            return null;
        }
        var wasTriggeringEffectiveSource =
            string.Equals(publication.SourceReportId, source.Report.Id, StringComparison.Ordinal) &&
            string.Equals(publication.SourceStatus, "APPROVED", StringComparison.Ordinal) &&
            publication.SourceLifecycleRevision is not null &&
            publication.SourceLifecycleRevision < source.Report.LifecycleRevision &&
            publication.TotalReportCount > 0;
        if (sourceLedgerRevision is null &&
            pinnedRevisions.Count == 0 &&
            !wasTriggeringEffectiveSource)
            return null;

        var assignee = (source.Assignment.Assignees ?? [])
            .FirstOrDefault(item => string.Equals(
                item.UserId,
                source.Report.AssigneeUserId,
                StringComparison.Ordinal));
        var approvalEntry = (source.Report.LifecycleProjectionOutbox ?? [])
            .FirstOrDefault(item => string.Equals(
                item.EntryKey,
                publication.SourceLifecycleEventKey,
                StringComparison.Ordinal)) ?? source.Entry;
        var runtime = new DirectRuntimePin(
            publication.FlowFamilyRevision,
            publication.FlowTemplateVersionId,
            publication.FlowContributionOriginVersionId ??
            sourceLedger?.MappingFlowVersionId,
            publication.FlowPayloadHash,
            publication.FlowCatalogVersion,
            publication.FlowCatalogSemanticHash,
            publication.FlowInstanceId,
            publication.FlowInstanceRevision,
            publication.FlowInstanceState,
            publication.FlowExecutionEpochId,
            publication.FlowExecutionEpoch,
            publication.FlowExecutionEpochRevision,
            publication.FlowExecutionEpochState,
            publication.FlowStepInstanceId,
            publication.FlowStepInstanceRevision,
            publication.FlowStepInstanceState,
            publication.FlowContributionPolicy,
            publication.FlowContributionPolicyHash,
            publication.FlowContributionWarning);
        var tenant = new DirectTenantPin(
            publication.ActorUserId ?? source.Entry.ActorUserId,
            publication.TenantUnitId ?? source.Period.AssigneeUnitId,
            source.Assignment.IssuedByUnitId,
            (source.Assignment.TargetUnitIds ?? []).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            source.Report.AssigneeUserId,
            assignee?.UnitId ?? source.Period.AssigneeUnitId);
        var isReplay = string.Equals(
            prior[0].ReversalAudit?.EventKey,
            source.Entry.EntryKey,
            StringComparison.Ordinal);
        return new ReversalContext(
            publication,
            new DirectMember(
                source.Report,
                source.Assignment,
                source.Period,
                approvalEntry,
                tenant,
                null,
                runtime),
            isReplay);
    }

    private async Task<IReadOnlyList<int>> LoadDirectSourceLifecycleRevisionsAsync(
        string generationId,
        string sourceReportId,
        CancellationToken ct)
    {
        var field = await _ctx.WorkReportFieldStatValues
            .Find(row => row.DirectProjection != null &&
                         row.DirectProjection.GenerationId == generationId &&
                         row.DirectProjection.SourceReportId == sourceReportId &&
                         !row.IsDeleted)
            .Project(row => row.DirectProjection!.SourceLifecycleRevision)
            .ToListAsync(ct);
        var table = await _ctx.WorkReportTableStatValues
            .Find(row => row.DirectProjection != null &&
                         row.DirectProjection.GenerationId == generationId &&
                         row.DirectProjection.SourceReportId == sourceReportId &&
                         !row.IsDeleted)
            .Project(row => row.DirectProjection!.SourceLifecycleRevision)
            .ToListAsync(ct);
        var label = await _ctx.WorkReportLabelStatValues
            .Find(row => row.DirectProjection != null &&
                         row.DirectProjection.GenerationId == generationId &&
                         row.DirectProjection.SourceReportId == sourceReportId &&
                         !row.IsDeleted)
            .Project(row => row.DirectProjection!.SourceLifecycleRevision)
            .ToListAsync(ct);
        return field
            .Concat(table)
            .Concat(label)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
    }

    private async Task ValidateCompletedPublicationAsync(
        WorkReportStatisticRebuildJob job,
        bool requireCurrent,
        CancellationToken ct)
    {
        if (!ObjectId.TryParse(job.Id, out _) ||
            !string.Equals(job.Status, WorkReportStatisticRebuildJobStatuses.Completed, StringComparison.Ordinal) ||
            job.IsActive ||
            (requireCurrent && !job.IsCurrentPublication) ||
            !job.PublishedAtUtc.HasValue ||
            !job.CompletedAtUtc.HasValue ||
            !job.ComputedAtUtc.HasValue ||
            job.DirectPublicationRevision is not > 0 ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.GenerationId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.GenerationHash) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.SourceMembershipSignature) ||
            !string.Equals(
                job.StateHash,
                BuildStateHash(
                    job.Id,
                    WorkReportStatisticRebuildJobStatuses.Completed,
                    job.StateRevision,
                    null,
                    null,
                    job.GenerationId,
                    job.GenerationHash),
                StringComparison.Ordinal))
        {
            throw Fail("CURRENT_PUBLICATION_INVALID");
        }

        var stored = (job.DirectStoreDigests ?? [])
            .OrderBy(item => item.Store, StringComparer.Ordinal)
            .ToArray();
        var observed = (await ComputePublishedJobDigestsAsync(job, ct))
            .OrderBy(item => item.Store, StringComparer.Ordinal)
            .ToArray();
        if (stored.Length != 6 ||
            stored.Zip(observed).Any(pair =>
                !string.Equals(pair.First.Store, pair.Second.Store, StringComparison.Ordinal) ||
                pair.First.RowCount != pair.Second.RowCount ||
                !string.Equals(pair.First.Sha256, pair.Second.Sha256, StringComparison.Ordinal)))
        {
            throw Fail("CURRENT_PUBLICATION_DIGEST_MISMATCH");
        }

        if (job.FlowContributionSourceCount != (job.FlowContributionSources ?? []).Count ||
            job.NonFlowContributionSourceCount !=
                (job.NonFlowContributionSources ?? []).Count ||
            job.FlowContributionTargetCount != (job.FlowContributionTargets ?? []).Count)
        {
            throw Fail("CURRENT_PUBLICATION_LEDGER_SHAPE_INVALID");
        }
        ValidatePersistedFlowContributionAudit(job);
        var v1Hash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_DIRECT_GENERATION_HASH_V1",
            job.GenerationId,
            job.SourceMembershipSignature,
            stores = observed.Select(item => new { item.Store, item.RowCount, item.Sha256 }).ToArray()
        });
        var v2Hash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_DIRECT_GENERATION_HASH_V2",
            job.GenerationId,
            job.SourceMembershipSignature,
            LedgerHash = job.FlowContributionLedgerHash,
            ReversalBaselineHash = job.FlowContributionReversalBaselineHash,
            stores = observed.Select(item => new { item.Store, item.RowCount, item.Sha256 }).ToArray()
        });
        var v3Hash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_DIRECT_GENERATION_HASH_V3",
            job.GenerationId,
            job.SourceMembershipSignature,
            LedgerHash = job.FlowContributionLedgerHash,
            ReversalBaselineHash = job.FlowContributionReversalBaselineHash,
            stores = observed.Select(item => new { item.Store, item.RowCount, item.Sha256 }).ToArray()
        });
        var reversalHash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_DIRECT_REVERSAL_GENERATION_HASH_V1",
            job.GenerationId,
            job.SourceMembershipSignature,
            LedgerHash = job.FlowContributionLedgerHash,
            ReversalBaselineHash = job.FlowContributionReversalBaselineHash,
            reversalAuditHash = job.ReversalAudit?.AuditHash,
            stores = observed.Select(item => new { item.Store, item.RowCount, item.Sha256 }).ToArray()
        });
        var reversalHashV2 = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_DIRECT_REVERSAL_GENERATION_HASH_V2",
            job.GenerationId,
            job.SourceMembershipSignature,
            LedgerHash = job.FlowContributionLedgerHash,
            ReversalBaselineHash = job.FlowContributionReversalBaselineHash,
            reversalAuditHash = job.ReversalAudit?.AuditHash,
            stores = observed.Select(item => new { item.Store, item.RowCount, item.Sha256 }).ToArray()
        });
        var contributionV2 = string.Equals(
            job.FlowContributionOperationVersion,
            ContributionOperationVersion,
            StringComparison.Ordinal);
        var generationHashValid = contributionV2
            ? job.ReversalAudit is null
                ? string.Equals(job.GenerationHash, v3Hash, StringComparison.Ordinal)
                : string.Equals(job.GenerationHash, reversalHashV2, StringComparison.Ordinal)
            : string.Equals(job.GenerationHash, v1Hash, StringComparison.Ordinal) ||
              string.Equals(job.GenerationHash, v2Hash, StringComparison.Ordinal) ||
              string.Equals(job.GenerationHash, reversalHash, StringComparison.Ordinal);
        if (job.NativeStatisticPublication is not null)
        {
            _ = await NativeStatisticPublicationContract.ReadAsync(_ctx.Db, job, ct);
            generationHashValid = contributionV2 && string.Equals(job.GenerationHash,
                NativeStatisticPublicationContract.BindHash(
                    job.ReversalAudit is null ? v3Hash : reversalHashV2, job.NativeStatisticPublication),
                StringComparison.Ordinal);
        }
        if (!generationHashValid)
        {
            throw Fail("CURRENT_PUBLICATION_GENERATION_HASH_MISMATCH");
        }
    }

    private async Task MarkPriorPublicationStaleAsync(
        WorkReportStatisticRebuildJob prior,
        string actorUserId,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            fb.Eq(x => x.Id, prior.Id) &
            fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.Completed) &
            fb.Eq(x => x.IsCurrentPublication, true) &
            fb.Eq(x => x.DirectPublicationRevision, prior.DirectPublicationRevision) &
            fb.Eq(x => x.GenerationId, prior.GenerationId) &
            fb.Eq(x => x.GenerationHash, prior.GenerationHash) &
            fb.Eq(x => x.IsDeleted, false),
            Builders<WorkReportStatisticRebuildJob>.Update
                .Set(x => x.FreshnessState, WorkReportStatisticRebuildJobFreshnessStates.Stale)
                .Set(x => x.StaleReason, "LIFECYCLE_REVERSAL_PENDING")
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
        if (result.MatchedCount != 1)
            throw Fail("REVERSAL_PRIOR_PUBLICATION_STALE");
    }

    private static WorkReportLifecycleProjectionOutboxEntry RequireCanonicalApprovalEntry(
        WorkAssignmentReport report)
    {
        var matches = (report.LifecycleProjectionOutbox ??
                       new List<WorkReportLifecycleProjectionOutboxEntry>())
            .Where(entry =>
                string.Equals(entry.CommandId, report.LastLifecycleCommandId, StringComparison.Ordinal) &&
                entry.LifecycleRevision == report.LastLifecycleCommandRevision &&
                string.Equals(entry.Operation, report.LastLifecycleCommandOperation, StringComparison.Ordinal))
            .ToList();
        if (matches.Count != 1)
            throw Fail("MEMBER_APPROVAL_EVENT_AMBIGUOUS");
        ValidateLifecycleEntry(report, matches[0]);
        return matches[0];
    }

    private async Task<DirectTenantPin> ResolveMemberTenantPinAsync(
        WorkAssignmentReport report,
        WorkAssignment assignment,
        WorkReportPeriod period,
        WorkReportLifecycleProjectionOutboxEntry approvalEntry,
        CancellationToken ct)
    {
        var approvalActorUserId = RequireObjectId(
            approvalEntry.ActorUserId,
            "MEMBER_APPROVAL_ACTOR_INVALID");
        if (!string.Equals(report.ApprovedByUserId, approvalActorUserId, StringComparison.Ordinal))
            throw Fail("MEMBER_APPROVAL_ACTOR_STALE");

        var actor = await _ctx.Users
            .Find(x => x.Id == approvalActorUserId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw Fail("MEMBER_APPROVAL_ACTOR_MISSING");
        var approvalActorUnitId = RequireObjectId(
            actor.UnitId,
            "MEMBER_APPROVAL_UNIT_INVALID");
        var issuedByUnitId = RequireObjectId(
            assignment.IssuedByUnitId,
            "MEMBER_ISSUER_UNIT_INVALID");
        var targetUnitIds = (assignment.TargetUnitIds ?? new List<string>())
            .Select(value => RequireObjectId(value, "MEMBER_TARGET_UNIT_INVALID"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (!string.Equals(approvalActorUnitId, issuedByUnitId, StringComparison.Ordinal) &&
            !targetUnitIds.Contains(approvalActorUnitId, StringComparer.Ordinal))
        {
            throw Fail("MEMBER_APPROVAL_TENANT_INVALID");
        }

        var assignee = (assignment.Assignees ?? new List<UserRef>())
            .SingleOrDefault(item => string.Equals(
                item.UserId,
                report.AssigneeUserId,
                StringComparison.Ordinal))
            ?? throw Fail("MEMBER_ASSIGNEE_MISSING");
        var assigneeUnitId = RequireObjectId(
            assignee.UnitId,
            "MEMBER_ASSIGNEE_UNIT_INVALID");
        if (!string.Equals(period.AssigneeUserId, report.AssigneeUserId, StringComparison.Ordinal) ||
            !string.Equals(period.AssigneeUnitId, assigneeUnitId, StringComparison.Ordinal) ||
            !targetUnitIds.Contains(assigneeUnitId, StringComparer.Ordinal))
        {
            throw Fail("MEMBER_ASSIGNEE_TENANT_INVALID");
        }

        return new DirectTenantPin(
            approvalActorUserId,
            approvalActorUnitId,
            issuedByUnitId,
            targetUnitIds,
            report.AssigneeUserId,
            assigneeUnitId);
    }

    private async Task<DirectRuntimePin?> ResolveEffectiveRuntimeAsync(
        WorkAssignmentReport report,
        WorkAssignment assignment,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
            return DirectRuntimePin.NonFlow;
        if (!string.Equals(assignment.FlowEffectiveStatus, "EFFECTIVE", StringComparison.Ordinal) ||
            !assignment.FlowExecutionEpoch.HasValue ||
            assignment.FlowExecutionEpoch < 1)
        {
            LogRuntimeAdmissionRejected(report.Id, "ASSIGNMENT_NOT_EFFECTIVE");
            return null;
        }
        if (!ObjectId.TryParse(assignment.FlowInstanceId, out _) ||
            !ObjectId.TryParse(assignment.FlowTemplateId, out _) ||
            assignment.FlowTemplateVersionNo is not > 0 ||
            !ObjectId.TryParse(assignment.FlowBranchId, out _) ||
            string.IsNullOrWhiteSpace(assignment.FlowStepId) ||
            assignment.FlowStepId.Length > 128 ||
            assignment.FlowStepId.Any(char.IsControl) ||
            assignment.FlowAttemptNo is not > 0)
        {
            LogRuntimeAdmissionRejected(report.Id, "ASSIGNMENT_RUNTIME_IDENTITY_INVALID");
            return null;
        }

        var flowFamily = await _ctx.DynamicFlowTemplates
            .Find(x => x.Id == assignment.FlowTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        var flowVersion = await _ctx.DynamicFlowTemplateVersions
            .Find(x => x.TemplateId == assignment.FlowTemplateId &&
                       x.VersionNo == assignment.FlowTemplateVersionNo &&
                       x.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                       !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        var instance = await _ctx.DynamicFlowInstances
            .Find(x => x.Id == assignment.FlowInstanceId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (flowFamily is null ||
            flowVersion is null ||
            instance is null ||
            flowFamily.FamilyRevision < 1 ||
            !string.Equals(
                flowFamily.Status,
                DynamicFlowTemplateStatuses.Active,
                StringComparison.Ordinal) ||
            !flowFamily.HasLockedVersion ||
            !StatRunCanonicalJson.IsCanonicalSha256(flowVersion.PayloadHash) ||
            string.IsNullOrWhiteSpace(flowVersion.CatalogVersion) ||
            !StatRunCanonicalJson.IsCanonicalSha256(flowVersion.CatalogSemanticHash) ||
            !string.Equals(
                flowVersion.RootDynamicFormTemplateId,
                report.DynamicFormTemplateId,
                StringComparison.Ordinal) ||
            !string.Equals(instance.WorkId, report.WorkId, StringComparison.Ordinal) ||
            !string.Equals(instance.FlowTemplateId, flowFamily.Id, StringComparison.Ordinal) ||
            !string.Equals(instance.FlowTemplateVersionId, flowVersion.Id, StringComparison.Ordinal) ||
            instance.FlowTemplateVersionNo != flowVersion.VersionNo ||
            !string.Equals(instance.FlowPayloadHash, flowVersion.PayloadHash, StringComparison.Ordinal) ||
            !string.Equals(instance.CatalogVersion, flowVersion.CatalogVersion, StringComparison.Ordinal) ||
            !string.Equals(
                instance.CatalogSemanticHash,
                flowVersion.CatalogSemanticHash,
                StringComparison.Ordinal) ||
            instance.ExecutionEpoch != assignment.FlowExecutionEpoch ||
            instance.State is not (
                DynamicFlowInstanceStates.Active or
                DynamicFlowInstanceStates.Reconciled or
                DynamicFlowInstanceStates.Completed or
                DynamicFlowInstanceStates.Finalized))
        {
            LogRuntimeAdmissionRejected(report.Id, "FLOW_ROOT_PIN_INVALID");
            return null;
        }
        try
        {
            DynamicFlowContributionPolicyContract.ValidateLockedPolicy(flowVersion);
        }
        catch (InvalidOperationException)
        {
            throw Fail("FLOW_CONTRIBUTION_POLICY_INVALID");
        }
        var epoch = await _ctx.DynamicFlowExecutionEpochs
            .Find(x => x.FlowInstanceId == instance.Id &&
                       x.ExecutionEpoch == assignment.FlowExecutionEpoch &&
                       !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (epoch is null ||
            !epoch.IsCanonical ||
            epoch.State is not (
                DynamicFlowExecutionEpochStates.Active or
                DynamicFlowExecutionEpochStates.Finalized))
        {
            LogRuntimeAdmissionRejected(report.Id, "FLOW_EPOCH_PIN_INVALID");
            return null;
        }
        var step = await _ctx.DynamicFlowStepInstances
            .Find(x => x.FlowInstanceId == instance.Id &&
                       x.AssignmentId == assignment.Id &&
                       !x.IsDeleted)
            .SortByDescending(x => x.Revision)
            .FirstOrDefaultAsync(ct);
        var stepPinFailure = step is null
            ? "FLOW_STEP_MISSING"
            : step.ExecutionEpoch != assignment.FlowExecutionEpoch.Value
                ? "FLOW_STEP_EXECUTION_EPOCH_STALE"
                : step.ReportId is not null &&
                  !string.Equals(step.ReportId, report.Id, StringComparison.Ordinal)
                    ? "FLOW_STEP_REPORT_STALE"
                    : step.IsCanonicalEpoch == false
                        ? "FLOW_STEP_NOT_CANONICAL"
                        : step.State is not (DynamicFlowStepStates.Approved or DynamicFlowStepStates.Completed)
                            ? "FLOW_STEP_STATE_NOT_EFFECTIVE"
                            : step.InvalidatedAtUtc.HasValue
                                ? "FLOW_STEP_INVALIDATED_AT"
                                : !string.IsNullOrWhiteSpace(step.InvalidatedByFlowEventId)
                                    ? "FLOW_STEP_INVALIDATED_EVENT"
                                    : !string.IsNullOrWhiteSpace(step.SupersededByStepInstanceId)
                                        ? "FLOW_STEP_SUPERSEDED"
                                        : step.ReportLifecycleRevision != report.LifecycleRevision
                                            ? "FLOW_STEP_LIFECYCLE_REVISION_STALE"
                                            : !string.Equals(step.ReportLifecycleStatus, "APPROVED", StringComparison.Ordinal)
                                                ? "FLOW_STEP_LIFECYCLE_STATUS_STALE"
                                                : step.ReportLifecycleIsActive != true
                                                    ? "FLOW_STEP_LIFECYCLE_INACTIVE"
                                                    : !string.Equals(step.FlowStepId, assignment.FlowStepId, StringComparison.Ordinal)
                                                        ? "FLOW_STEP_ID_STALE"
                                                        : !string.Equals(step.BranchId, assignment.FlowBranchId, StringComparison.Ordinal)
                                                            ? "FLOW_STEP_BRANCH_STALE"
                                                            : step.AttemptNo != assignment.FlowAttemptNo
                                                                ? "FLOW_STEP_ATTEMPT_STALE"
                                                                : !string.Equals(step.FormFamilyId, report.DynamicFormFamilyId, StringComparison.Ordinal)
                                                                    ? "FLOW_STEP_FORM_FAMILY_STALE"
                                                                    : !string.Equals(step.FormVersionId, report.DynamicFormTemplateId, StringComparison.Ordinal)
                                                                        ? "FLOW_STEP_FORM_VERSION_STALE"
                                                                        : step.FormVersionNo != report.DynamicFormVersionNo
                                                                            ? "FLOW_STEP_FORM_VERSION_NO_STALE"
                                                                            : !string.Equals(step.FormSchemaHash, report.DynamicFormSchemaHash, StringComparison.Ordinal)
                                                                                ? "FLOW_STEP_FORM_SCHEMA_STALE"
                                                                                : null;
        if (stepPinFailure is not null)
        {
            LogRuntimeAdmissionRejected(report.Id, stepPinFailure);
            return null;
        }
        return new DirectRuntimePin(
            flowFamily.FamilyRevision,
            flowVersion.Id,
            flowVersion.OriginVersionId,
            flowVersion.PayloadHash,
            flowVersion.CatalogVersion,
            flowVersion.CatalogSemanticHash,
            instance.Id,
            instance.Revision,
            instance.State,
            epoch.Id,
            epoch.ExecutionEpoch,
            epoch.Revision,
            epoch.State,
            step.Id,
            step.Revision,
            step.State,
            flowVersion.ContributionPolicy ?? DynamicFlowContributionPolicyContract.Exclude,
            flowVersion.ContributionPolicyHash,
            flowVersion.ContributionWarning);
    }

    private void LogRuntimeAdmissionRejected(
        string reportId,
        string reason)
        => _logger.LogWarning(
            "P9 Direct runtime admission rejected. reportId={ReportId} reason={Reason}",
            reportId,
            reason);

    private static string BuildMembershipSignature(IReadOnlyCollection<DirectMember> members)
        => StatRunCanonicalJson.HashObject(new
        {
            version = "P9_DIRECT_MEMBERSHIP_V1",
            members = members
                .OrderBy(item => item.Report.Id, StringComparer.Ordinal)
                .Select(item => new
                {
                    reportId = item.Report.Id,
                    workId = item.Report.WorkId,
                    workAssignmentId = item.Report.WorkAssignmentId,
                    workReportPeriodId = item.Report.WorkReportPeriodId,
                    periodKey = item.Report.PeriodKey,
                    periodInstanceKey = item.Report.PeriodInstanceKey,
                    periodKind = item.Report.PeriodKind,
                    periodStartUtc = NormalizeUtc(item.Report.PeriodStart),
                    periodEndUtc = NormalizeUtc(item.Report.PeriodEnd),
                    payloadRevision = item.Report.PayloadRevision,
                    payloadHash = item.Report.PayloadHash,
                    lifecycleRevision = item.Report.LifecycleRevision,
                    lifecycleStatus = item.Report.Status.ToString(),
                    item.Report.IsCurrent,
                    item.Report.IsActive,
                    item.Report.InvalidatedByFlowEventId,
                    item.Report.CumulativeContributionMode,
                    item.Report.CumulativeContributionPolicyJson,
                    mappingReceiptId = item.Mapping?.ReceiptId,
                    mappingProvenanceId = item.Mapping?.ProvenanceId,
                    mappingProvenanceHash = item.Mapping?.ProvenanceHash,
                    mappingResultPayloadRevision = item.Mapping?.ResultPayloadRevision,
                    mappingResultPayloadHash = item.Mapping?.ResultPayloadHash,
                    item.Report.ApprovedByUserId,
                    item.Report.LastLifecycleCommandId,
                    item.Report.LastLifecycleCommandHash,
                    item.Report.LastLifecycleCommandOperation,
                    item.Report.LastLifecycleCommandRevision,
                    item.Report.LastLifecycleCommandPayloadRevision,
                    lastLifecycleCommandStatus = item.Report.LastLifecycleCommandStatus?.ToString(),
                    item.Report.LastLifecycleCommandIsActive,
                    approvalEntryKey = item.ApprovalEntry.EntryKey,
                    approvalCommandId = item.ApprovalEntry.CommandId,
                    approvalOperation = item.ApprovalEntry.Operation,
                    approvalActorUserId = item.ApprovalEntry.ActorUserId,
                    approvalFromStatus = item.ApprovalEntry.FromStatus,
                    approvalToStatus = item.ApprovalEntry.ToStatus,
                    approvalFromIsActive = item.ApprovalEntry.FromIsActive,
                    approvalToIsActive = item.ApprovalEntry.ToIsActive,
                    approvalPayloadRevision = item.ApprovalEntry.PayloadRevision,
                    approvalPayloadHash = item.ApprovalEntry.PayloadHash,
                    approvalCreatedAtUtc = ResolveComputedAtUtc(item.ApprovalEntry),
                    item.Report.DynamicFormFamilyId,
                    item.Report.DynamicFormTemplateId,
                    item.Report.DynamicFormVersionNo,
                    item.Report.DynamicFormSchemaHash,
                    assignmentId = item.Assignment.Id,
                    assignmentWorkId = item.Assignment.WorkId,
                    assignmentIsActive = item.Assignment.IsActive,
                    assignmentInvalidatedByFlowEventId = item.Assignment.InvalidatedByFlowEventId,
                    assignmentDynamicFormFamilyId = item.Assignment.DynamicFormFamilyId,
                    assignmentDynamicFormTemplateId = item.Assignment.DynamicFormTemplateId,
                    assignmentDynamicFormVersionNo = item.Assignment.DynamicFormVersionNo,
                    assignmentDynamicFormSchemaHash = item.Assignment.DynamicFormSchemaHash,
                    item.Assignment.CreatedByUserId,
                    item.Assignment.FlowInstanceId,
                    item.Assignment.FlowEffectiveStatus,
                    item.Assignment.FlowTemplateId,
                    item.Assignment.FlowTemplateVersionNo,
                    item.Assignment.FlowExecutionEpoch,
                    item.Assignment.FlowBranchId,
                    item.Assignment.FlowStepId,
                    item.Assignment.FlowAttemptNo,
                    tenantApprovalActorUserId = item.Tenant.ApprovalActorUserId,
                    item.Tenant.ApprovalActorUnitId,
                    item.Tenant.IssuedByUnitId,
                    item.Tenant.TargetUnitIds,
                    item.Tenant.AssigneeUserId,
                    item.Tenant.AssigneeUnitId,
                    periodStatus = item.Period.Status.ToString(),
                    periodIsActive = item.Period.IsActive,
                    item.Period.CurrentReportId,
                    item.Period.SourceLifecycleReportId,
                    item.Period.SourceLifecycleRevision,
                    periodSourceLifecycleAppliedAtUtc = NormalizeUtc(item.Period.SourceLifecycleAppliedAtUtc),
                    item.Runtime.FlowFamilyRevision,
                    item.Runtime.FlowTemplateVersionId,
                    item.Runtime.FlowContributionOriginVersionId,
                    item.Runtime.FlowPayloadHash,
                    item.Runtime.FlowCatalogVersion,
                    item.Runtime.FlowCatalogSemanticHash,
                    runtimeFlowInstanceId = item.Runtime.FlowInstanceId,
                    item.Runtime.FlowInstanceRevision,
                    item.Runtime.FlowInstanceState,
                    item.Runtime.ExecutionEpochId,
                    item.Runtime.ExecutionEpoch,
                    item.Runtime.ExecutionEpochRevision,
                    item.Runtime.ExecutionEpochState,
                    item.Runtime.StepInstanceId,
                    item.Runtime.StepInstanceRevision,
                    item.Runtime.StepInstanceState,
                    item.Runtime.LockedContributionPolicy,
                    item.Runtime.LockedContributionPolicyHash,
                    item.Runtime.LockedContributionWarning
                })
                .ToArray()
        });

    private async Task<RunClaim> AcquireRunAsync(
        DirectSource source,
        DirectMember triggerMember,
        DynamicFormTemplate template,
        StatRunCandidateBinding binding,
        WorkReportDirectGenerationContext context,
        DirectProjectionIdentity identity,
        string publicationScopeKey,
        int memberCount,
        string actorUserId,
        string tenantUnitId,
        IReadOnlyCollection<DirectMember> members,
        ReversalContext? reversal,
        CancellationToken ct)
    {
        var existing = await _ctx.WorkReportStatisticRebuildJobs
            .Find(x => x.Id == context.RunId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (existing is not null &&
            string.Equals(existing.Status, WorkReportStatisticRebuildJobStatuses.Completed, StringComparison.Ordinal))
        {
            await ValidateCompletedReplayAsync(
                existing, context, source, triggerMember, template, binding,
                identity,
                publicationScopeKey, actorUserId, tenantUnitId, members,
                reversal, ct);
            return RunClaim.FromCompleted(existing);
        }
        if (existing is not null &&
            string.Equals(existing.Status, WorkReportStatisticRebuildJobStatuses.DeadLetter, StringComparison.Ordinal))
        {
            return RunClaim.FromTerminal(existing.StaleReason ?? "STALE_RUN_TERMINAL");
        }

        var now = DateTime.UtcNow;
        var workerId = $"{WorkerPrefix}:{Guid.NewGuid():N}";
        var claimToken = Guid.NewGuid().ToString("N");
        if (existing is null)
        {
            var receiptId = identity.ReceiptId;
            var reversalAudit = BuildReversalAudit(
                context,
                source,
                receiptId,
                reversal);
            string? immutableHeaderHash = identity.IsFoundationRefresh
                ? null
                : BuildImmutableHeaderHash(
                    context,
                    source,
                    triggerMember,
                    binding,
                    publicationScopeKey,
                    receiptId,
                    actorUserId,
                    tenantUnitId,
                    memberCount,
                    reversalAudit);
            var stateHash = BuildStateHash(
                context.RunId,
                WorkReportStatisticRebuildJobStatuses.Running,
                1,
                claimToken,
                workerId,
                context.GenerationId,
                null);
            var job = new WorkReportStatisticRebuildJob
            {
                Id = context.RunId,
                DedupeKey = identity.DedupeKey,
                ReceiptId = receiptId,
                CommandId = identity.IsFoundationRefresh ? null : source.Entry.CommandId,
                RequestHash = immutableHeaderHash,
                ImmutableHeaderHash = immutableHeaderHash,
                ReceiptAcceptedAtUtc = now,
                CapabilityId = StatRunCapabilities.DirectFieldTableLabel,
                RouteId = StatRunRouteRegistry.LifecycleDirectProjector,
                RunKind = WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection,
                ActorUserId = actorUserId,
                TenantUnitId = tenantUnitId,
                ScopeType = "WORK_PERIOD_TEMPLATE",
                ScopeId = source.Report.WorkId,
                SourceReportId = source.Report.Id,
                SourcePayloadRevision = source.Report.PayloadRevision,
                SourcePayloadHash = source.Report.PayloadHash,
                SourceLifecycleRevision = source.Report.LifecycleRevision,
                SourceLifecycleEventKey = source.Entry.EntryKey,
                DirectProjectionIdentityVersion = identity.Version,
                DirectProjectionIdentityKey = identity.Key,
                SourceMembershipSignature = context.SourceMembershipSignature,
                DirectSourceRevision = context.DirectSourceRevision,
                PublicationScopeKey = publicationScopeKey,
                IsCurrentPublication = false,
                SourceStatus = source.Report.Status.ToString().ToUpperInvariant(),
                ConfigId = context.ConfigId,
                ConfigVersionId = context.ConfigVersionId,
                ConfigVersionNo = context.ConfigVersionNo,
                ConfigRevision = context.ConfigRevision,
                ConfigHash = context.ConfigHash,
                CatalogVersion = binding.CatalogVersion,
                CatalogRawSha256 = binding.CatalogRawSha256,
                CatalogSemanticSha256 = binding.CatalogSemanticSha256,
                SchemaRawSha256 = binding.SchemaRawSha256,
                SchemaSemanticSha256 = binding.SchemaSemanticSha256,
                StageLockSha256 = binding.StageLockSha256,
                CandidateChainId = binding.ChainId,
                CandidatePromptId = binding.PromptId,
                DynamicFormFamilyId = context.DynamicFormFamilyId,
                DynamicFormTemplateId = template.Id,
                DynamicFormVersionNo = context.DynamicFormVersionNo,
                DynamicFormSchemaHash = context.DynamicFormSchemaHash,
                DynamicFormTemplateCode = template.Code,
                DynamicFormTemplateName = template.Name,
                ScopeKind = WorkReportStatisticRebuildJobScopeKinds.Bounded,
                WorkId = source.Report.WorkId,
                WorkAssignmentId = source.Report.WorkAssignmentId,
                FlowInstanceId = triggerMember.Runtime.FlowInstanceId,
                FlowInstanceRevision = triggerMember.Runtime.FlowInstanceRevision,
                FlowInstanceState = triggerMember.Runtime.FlowInstanceState,
                FlowEffectiveStatus = source.Assignment.FlowEffectiveStatus,
                FlowTemplateId = source.Assignment.FlowTemplateId,
                FlowFamilyRevision = triggerMember.Runtime.FlowFamilyRevision,
                FlowTemplateVersionNo = source.Assignment.FlowTemplateVersionNo,
                FlowTemplateVersionId = triggerMember.Runtime.FlowTemplateVersionId,
                FlowContributionOriginVersionId =
                    triggerMember.Runtime.FlowContributionOriginVersionId,
                FlowPayloadHash = triggerMember.Runtime.FlowPayloadHash,
                FlowCatalogVersion = triggerMember.Runtime.FlowCatalogVersion,
                FlowCatalogSemanticHash = triggerMember.Runtime.FlowCatalogSemanticHash,
                FlowExecutionEpoch = source.Assignment.FlowExecutionEpoch,
                FlowExecutionEpochId = triggerMember.Runtime.ExecutionEpochId,
                FlowExecutionEpochRevision = triggerMember.Runtime.ExecutionEpochRevision,
                FlowExecutionEpochState = triggerMember.Runtime.ExecutionEpochState,
                FlowBranchId = source.Assignment.FlowBranchId,
                FlowStepId = source.Assignment.FlowStepId,
                FlowAttemptNo = source.Assignment.FlowAttemptNo,
                FlowStepInstanceId = triggerMember.Runtime.StepInstanceId,
                FlowStepInstanceRevision = triggerMember.Runtime.StepInstanceRevision,
                FlowStepInstanceState = triggerMember.Runtime.StepInstanceState,
                FlowContributionPolicy = triggerMember.Runtime.LockedContributionPolicy,
                FlowContributionPolicyHash = triggerMember.Runtime.LockedContributionPolicyHash,
                FlowContributionWarning = triggerMember.Runtime.LockedContributionWarning,
                FlowContributionOperationVersion = ContributionOperationVersion,
                ReversalAudit = reversalAudit,
                PeriodKey = source.Report.PeriodKey,
                PeriodInstanceKey = source.Report.PeriodInstanceKey,
                PeriodKind = source.Report.PeriodKind,
                PeriodStartUtc = NormalizeUtc(source.Report.PeriodStart),
                PeriodEndUtc = NormalizeUtc(source.Report.PeriodEnd),
                Status = WorkReportStatisticRebuildJobStatuses.Running,
                StateRevision = 1,
                StateHash = stateHash,
                IsActive = true,
                RequestedByUserId = actorUserId,
                Priority = WorkReportStatisticRebuildJobPriorities.High,
                TotalReportCount = memberCount,
                ProcessedReportCount = 0,
                RetryCount = 0,
                LeaseUntilUtc = now.Add(ClaimLease),
                ClaimToken = claimToken,
                LeaseOwnerId = workerId,
                ClaimedAtUtc = now,
                LastHeartbeatAtUtc = now,
                DeadlineAtUtc = now.Add(ClaimLease),
                InitialDeadlineAtUtc = now.Add(ClaimLease),
                LastRunAtUtc = now,
                GenerationId = context.GenerationId,
                FreshnessState = WorkReportStatisticRebuildJobFreshnessStates.Pending,
                ComputedAtUtc = context.ComputedAtUtc,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = actorUserId,
                UpdatedByUserId = actorUserId,
                IsDeleted = false
            };
            if (identity.IsFoundationRefresh)
            {
                immutableHeaderHash = ComputeFoundationRefreshImmutableHeaderHash(job);
                job.RequestHash = immutableHeaderHash;
                job.ImmutableHeaderHash = immutableHeaderHash;
            }
            try
            {
                await _ctx.WorkReportStatisticRebuildJobs.InsertOneAsync(job, cancellationToken: ct);
                return new RunClaim(job, workerId, claimToken, null, null);
            }
            catch (MongoWriteException exception) when (
                exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                existing = await _ctx.WorkReportStatisticRebuildJobs
                    .Find(x => x.Id == context.RunId && !x.IsDeleted)
                    .FirstOrDefaultAsync(ct)
                    ?? throw Fail("RUN_IDENTITY_COLLISION");
                if (string.Equals(
                        existing.Status,
                        WorkReportStatisticRebuildJobStatuses.Completed,
                        StringComparison.Ordinal))
                {
                    await ValidateCompletedReplayAsync(
                        existing, context, source, triggerMember, template, binding,
                        identity,
                        publicationScopeKey, actorUserId, tenantUnitId, members,
                        reversal, ct);
                    return RunClaim.FromCompleted(existing);
                }
            }
        }

        try
        {
            ValidateRunHeader(
                existing!, context, source, triggerMember, template, binding,
                identity, publicationScopeKey, actorUserId, tenantUnitId, memberCount,
                BuildReversalAudit(
                    context,
                    source,
                    existing!.ReceiptId ?? throw Fail("RUN_RECEIPT_MISSING"),
                    reversal));
        }
        catch (InvalidOperationException exception) when (
            string.Equals(exception.Message, "P9_DIRECT_RUN_HEADER_CONFLICT", StringComparison.Ordinal))
        {
            var completed = await TerminalizeStaleRunAsync(
                existing!,
                "STALE_RUN_HEADER",
                source.Report.Id,
                context.LifecycleEventKey,
                ct);
            return completed is null
                ? RunClaim.FromTerminal("STALE_RUN_HEADER")
                : RunClaim.FromCompleted(completed);
        }
        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var eligible = fb.Eq(x => x.Id, context.RunId) &
                       fb.Eq(x => x.RunKind, WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection) &
                       fb.Eq(x => x.SourceReportId, source.Report.Id) &
                       fb.Eq(x => x.SourceLifecycleEventKey, context.LifecycleEventKey) &
                       IdentityFence(fb, identity) &
                       fb.Eq(x => x.DirectSourceRevision, context.DirectSourceRevision) &
                       fb.Eq(x => x.PublicationScopeKey, publicationScopeKey) &
                       fb.Eq(x => x.GenerationId, context.GenerationId) &
                       fb.Eq(x => x.IsDeleted, false) &
                       fb.Eq(x => x.IsActive, true) &
                       (fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.Pending) |
                        fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.RetryWaiting) |
                        (fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.Running) &
                          fb.Lte(x => x.LeaseUntilUtc, now))) &
                       (fb.Eq(x => x.NextRetryAtUtc, null) |
                        fb.Lte(x => x.NextRetryAtUtc, now)) &
                       fb.Eq(x => x.StateRevision, existing!.StateRevision) &
                       fb.Eq(x => x.StateHash, existing.StateHash);
        var revision = existing.StateRevision + 1;
        var state = BuildStateHash(
            existing.Id,
            WorkReportStatisticRebuildJobStatuses.Running,
            revision,
            claimToken,
            workerId,
            context.GenerationId,
            null);
        var claimed = await _ctx.WorkReportStatisticRebuildJobs.FindOneAndUpdateAsync(
            eligible,
            Builders<WorkReportStatisticRebuildJob>.Update
                .Set(x => x.Status, WorkReportStatisticRebuildJobStatuses.Running)
                .Set(x => x.ClaimToken, claimToken)
                .Set(x => x.LeaseOwnerId, workerId)
                .Set(x => x.ClaimedAtUtc, now)
                .Set(x => x.LastHeartbeatAtUtc, now)
                .Set(x => x.LeaseUntilUtc, now.Add(ClaimLease))
                .Set(x => x.LastRunAtUtc, now)
                .Set(x => x.NextRetryAtUtc, null)
                .Set(x => x.StateRevision, revision)
                .Set(x => x.StateHash, state)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            new FindOneAndUpdateOptions<WorkReportStatisticRebuildJob>
            {
                ReturnDocument = ReturnDocument.After
            },
            ct);
        if (claimed is null)
            throw Fail("RUN_CLAIM_BUSY");
        return new RunClaim(claimed, workerId, claimToken, null, null);
    }

    private async Task RevalidateBeforePublishAsync(
        DirectSource expected,
        DynamicFormTemplate expectedTemplate,
        StatRunCandidateBinding expectedBinding,
        WorkReportDirectGenerationContext context,
        string expectedMembershipSignature,
        bool expectSourceEffective,
        CancellationToken ct)
    {
        var binding = _activation.RequireFoundation(
            StatRunCapabilities.DirectFieldTableLabel,
            StatRunRouteRegistry.LifecycleDirectProjector);
        if (binding != expectedBinding)
            throw Fail("CANDIDATE_PIN_STALE");
        var observed = await LoadSourceAsync(
            expected.Report.Id,
            expected.Entry.EntryKey,
            ct);
        var template = await LoadLockedTemplateAsync(observed.Report, observed.Assignment, ct);
        ValidateSourcePeriod(observed.Report, observed.Period);
        if (!string.Equals(template.Id, expectedTemplate.Id, StringComparison.Ordinal) ||
            !string.Equals(template.FamilyId, context.DynamicFormFamilyId, StringComparison.Ordinal) ||
            !string.Equals(template.Id, context.DynamicFormTemplateId, StringComparison.Ordinal) ||
            template.VersionNo != context.DynamicFormVersionNo ||
            !string.Equals(template.PublishedSchemaHash, context.DynamicFormSchemaHash, StringComparison.Ordinal) ||
            !string.Equals(template.StatisticConfigId, context.ConfigId, StringComparison.Ordinal) ||
            !string.Equals(template.StatisticConfigVersionId, context.ConfigVersionId, StringComparison.Ordinal) ||
            template.StatisticConfigVersionNo != context.ConfigVersionNo ||
            template.StatisticConfigRevision != context.ConfigRevision ||
            !string.Equals(template.StatisticConfigHash, context.ConfigHash, StringComparison.Ordinal))
        {
            throw Fail("CONFIG_PIN_STALE");
        }
        var work = await _ctx.Works
            .Find(x => x.Id == observed.Report.WorkId &&
                       x.DirectSourceRevision == context.DirectSourceRevision &&
                       !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (work is null)
            throw Fail("SOURCE_REVISION_STALE");
        var members = await ResolveMembershipAsync(
            observed.Report.WorkId,
            observed.Report.PeriodInstanceKey,
            template,
            ct);
        var containsSource = members.Any(item =>
            string.Equals(item.Report.Id, observed.Report.Id, StringComparison.Ordinal));
        if (containsSource != expectSourceEffective ||
            !string.Equals(
                BuildProjectionMembershipSignature(members, template),
                expectedMembershipSignature,
                StringComparison.Ordinal))
        {
            throw Fail("MEMBERSHIP_STALE");
        }
    }

    internal static FilterDefinition<WorkReportStatisticRebuildJob>
        BuildCurrentPublicationFamilyFilter(
            FilterDefinitionBuilder<WorkReportStatisticRebuildJob> filter,
            WorkReportStatisticRebuildJob job)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(job);
        if (!ObjectId.TryParse(job.WorkId, out _) ||
            string.IsNullOrWhiteSpace(job.PeriodInstanceKey) ||
            string.IsNullOrWhiteSpace(job.PeriodKind) ||
            !ObjectId.TryParse(job.DynamicFormFamilyId, out _) ||
            !ObjectId.TryParse(job.DynamicFormTemplateId, out _) ||
            job.DynamicFormVersionNo is not > 0 ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.DynamicFormSchemaHash) ||
            string.IsNullOrWhiteSpace(job.CandidateChainId) ||
            string.IsNullOrWhiteSpace(job.CandidatePromptId))
        {
            throw Fail("CURRENT_PUBLICATION_FAMILY_INVALID");
        }

        return filter.Eq(
                   value => value.RunKind,
                   WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection) &
               filter.Eq(value => value.WorkId, job.WorkId) &
               filter.Eq(value => value.PeriodInstanceKey, job.PeriodInstanceKey) &
               filter.Eq(value => value.PeriodKind, job.PeriodKind) &
               filter.Eq(
                   value => value.DynamicFormFamilyId,
                   job.DynamicFormFamilyId) &
               filter.Eq(
                   value => value.DynamicFormTemplateId,
                   job.DynamicFormTemplateId) &
               filter.Eq(
                   value => value.DynamicFormVersionNo,
                   job.DynamicFormVersionNo) &
               filter.Eq(
                   value => value.DynamicFormSchemaHash,
                   job.DynamicFormSchemaHash) &
               filter.Eq(value => value.CandidateChainId, job.CandidateChainId) &
               filter.Eq(value => value.CandidatePromptId, job.CandidatePromptId) &
               filter.Eq(value => value.IsCurrentPublication, true) &
               filter.Eq(value => value.IsDeleted, false);
    }

    internal static UpdateDefinition<WorkReportStatisticRebuildJob>
        BuildCurrentPublicationSupersessionUpdate(
            string actorUserId,
            DateTime updatedAtUtc)
        => Builders<WorkReportStatisticRebuildJob>.Update
            .Set(value => value.IsCurrentPublication, false)
            .Set(
                value => value.FreshnessState,
                WorkReportStatisticRebuildJobFreshnessStates.Stale)
            .Set(value => value.StaleReason, "LIFECYCLE_SUPERSEDED")
            .Set(value => value.UpdatedAtUtc, updatedAtUtc)
            .Set(value => value.UpdatedByUserId, actorUserId);

    private async Task SupersedeCurrentPublicationFamilyAsync(
        IClientSessionHandle session,
        WorkReportStatisticRebuildJob job,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        var filter = Builders<WorkReportStatisticRebuildJob>.Filter;
        var family = BuildCurrentPublicationFamilyFilter(filter, job) &
                     filter.Ne(value => value.Id, job.Id);
        var current = await _ctx.WorkReportStatisticRebuildJobs
            .Find(session, family)
            .SortByDescending(value => value.DirectPublicationRevision)
            .ThenBy(value => value.Id)
            .Limit(2)
            .ToListAsync(ct);
        if (current.Count > 1)
            throw Fail("CURRENT_PUBLICATION_AMBIGUOUS");
        if (current.Count == 0)
            return;

        var prior = current[0];
        var expectedNativeReversal = prior.NativeStatisticPublication is not null &&
            job.ReversalAudit is { } inverse && inverse.PriorRunId == prior.Id &&
            inverse.PriorGenerationId == prior.GenerationId && inverse.PriorGenerationHash == prior.GenerationHash &&
            inverse.PriorLedgerHash == prior.FlowContributionLedgerHash &&
            prior.FreshnessState == WorkReportStatisticRebuildJobFreshnessStates.Stale &&
            prior.StaleReason == "LIFECYCLE_REVERSAL_PENDING";
        if (!string.Equals(
                prior.Status,
                WorkReportStatisticRebuildJobStatuses.Completed,
                StringComparison.Ordinal) ||
            prior.IsActive ||
            prior.DirectPublicationRevision is not > 0 ||
            !StatRunCanonicalJson.IsCanonicalSha256(prior.GenerationId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(prior.GenerationHash) ||
            (!expectedNativeReversal && !string.Equals(
                prior.FreshnessState,
                WorkReportStatisticRebuildJobFreshnessStates.Fresh,
                StringComparison.Ordinal)) ||
            !string.Equals(
                prior.StateHash,
                BuildStateHash(
                    prior.Id,
                    WorkReportStatisticRebuildJobStatuses.Completed,
                    prior.StateRevision,
                    null,
                    null,
                    prior.GenerationId,
                    prior.GenerationHash),
                StringComparison.Ordinal))
        {
            throw Fail("CURRENT_PUBLICATION_INVALID");
        }

        var fence = family &
                    filter.Eq(value => value.Id, prior.Id) &
                    filter.Eq(
                        value => value.Status,
                        WorkReportStatisticRebuildJobStatuses.Completed) &
                    filter.Eq(value => value.IsActive, false) &
                    filter.Eq(
                        value => value.DirectPublicationRevision,
                        prior.DirectPublicationRevision) &
                    filter.Eq(value => value.GenerationId, prior.GenerationId) &
                    filter.Eq(value => value.GenerationHash, prior.GenerationHash) &
                    filter.Eq(value => value.StateRevision, prior.StateRevision) &
                    filter.Eq(value => value.StateHash, prior.StateHash) &
                    filter.Eq(
                        value => value.FreshnessState,
                        prior.FreshnessState) &
                    filter.Eq(value => value.StaleReason, prior.StaleReason);
        var result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            session,
            fence,
            BuildCurrentPublicationSupersessionUpdate(actorUserId, now),
            cancellationToken: ct);
        if (result.MatchedCount != 1 || result.ModifiedCount != 1)
            throw Fail("STALE_PUBLICATION_FENCE");
    }
    private async Task<WorkReportStatisticRebuildJob> PublishAsync(
        WorkReportStatisticRebuildJob job,
        string workerId,
        string claimToken,
        DirectSource source,
        WorkReportDirectGenerationContext context,
        DirectProjectionIdentity identity,
        string generationHash,
        IReadOnlyCollection<WorkReportDirectStoreDigest> digests,
        FlowContributionAudit contributionAudit,
        WorkReportStatisticReversalAudit? reversalAudit,
        NativeProjectionStage? native,
        int memberCount,
        string actorUserId,
        CancellationToken ct)
    {
        return await _transactionRunner.ExecuteAsync(
            async (session, transactionCt) =>
            {
                if (native is not null)
                    await FenceNativeProjectionAsync(session, native, transactionCt);
                var now = DateTime.UtcNow;
                var nextRevision = job.StateRevision + 1;
                var nextStateHash = BuildStateHash(
                    job.Id,
                    WorkReportStatisticRebuildJobStatuses.Completed,
                    nextRevision,
                    null,
                    null,
                    context.GenerationId,
                    generationHash);

                var wf = Builders<Work>.Filter;
                var work = await _ctx.Works.FindOneAndUpdateAsync(
                    session,
                    wf.Eq(x => x.Id, source.Report.WorkId) &
                    wf.Eq(x => x.DirectSourceRevision, context.DirectSourceRevision) &
                    wf.Eq(x => x.IsDeleted, false),
                    Builders<Work>.Update.Inc(x => x.DirectPublicationRevision, 1),
                    new FindOneAndUpdateOptions<Work>
                    {
                        ReturnDocument = ReturnDocument.After
                    },
                    transactionCt)
                    ?? throw Fail("STALE_SOURCE_REVISION_FENCE");
                var publicationRevision = work.DirectPublicationRevision;
                if (publicationRevision < 1)
                    throw Fail("PUBLICATION_REVISION_INVALID");

                var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
                await SupersedeCurrentPublicationFamilyAsync(
                    session,
                    job,
                    actorUserId,
                    now,
                    transactionCt);

                var fence = fb.Eq(x => x.Id, job.Id) &
                            fb.Eq(x => x.RunKind, WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection) &
                            fb.Eq(x => x.SourceReportId, source.Report.Id) &
                            fb.Eq(x => x.SourceLifecycleEventKey, context.LifecycleEventKey) &
                            IdentityFence(fb, identity) &
                            fb.Eq(x => x.SourceMembershipSignature, context.SourceMembershipSignature) &
                            fb.Eq(x => x.DirectSourceRevision, context.DirectSourceRevision) &
                            fb.Eq(x => x.PublicationScopeKey, job.PublicationScopeKey) &
                             fb.Eq(x => x.GenerationId, context.GenerationId) &
                             (reversalAudit is null
                                 ? fb.Eq("reversalAudit", BsonNull.Value)
                                 : fb.Eq("reversalAudit.auditHash", reversalAudit.AuditHash)) &
                             fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.Running) &
                            fb.Eq(x => x.IsActive, true) &
                            fb.Eq(x => x.IsDeleted, false) &
                            fb.Eq(x => x.LeaseOwnerId, workerId) &
                            fb.Eq(x => x.ClaimToken, claimToken) &
                            fb.Gt(x => x.LeaseUntilUtc, now) &
                            fb.Eq(x => x.StateRevision, job.StateRevision) &
                            fb.Eq(x => x.StateHash, job.StateHash);
                var published = await _ctx.WorkReportStatisticRebuildJobs.FindOneAndUpdateAsync(
                    session,
                    fence,
                    Builders<WorkReportStatisticRebuildJob>.Update
                        .Set(x => x.Status, WorkReportStatisticRebuildJobStatuses.Completed)
                        .Set(x => x.IsActive, false)
                        .Set(x => x.IsCurrentPublication, true)
                        .Set(x => x.DirectPublicationRevision, publicationRevision)
                        .Set(x => x.ProcessedReportCount, memberCount)
                        .Set(x => x.NextRetryAtUtc, null)
                        .Set(x => x.LeaseUntilUtc, null)
                        .Set(x => x.ClaimToken, null)
                        .Set(x => x.LeaseOwnerId, null)
                        .Set(x => x.CompletedAtUtc, now)
                        .Set(x => x.ComputedAtUtc, context.ComputedAtUtc)
                        .Set(x => x.PublishedAtUtc, now)
                        .Set(x => x.GenerationHash, generationHash)
                        .Set(x => x.DirectStoreDigests, digests.ToList())
                        .Set(x => x.NativeStatisticPublication, native == null ? null : native.Publication)
                        .Set(x => x.FlowContributionLedgerHash, contributionAudit.LedgerHash)
                        .Set(x => x.FlowContributionReversalBaselineHash, contributionAudit.ReversalBaselineHash)
                        .Set(x => x.FlowContributionSourceCount, contributionAudit.Sources.Count)
                        .Set(x => x.NonFlowContributionSourceCount, contributionAudit.NonFlowSources.Count)
                        .Set(x => x.FlowContributionTargetCount, contributionAudit.Targets.Count)
                        .Set(x => x.FlowContributionSources, contributionAudit.Sources.ToList())
                        .Set(x => x.NonFlowContributionSources, contributionAudit.NonFlowSources.ToList())
                        .Set(x => x.FlowContributionTargets, contributionAudit.Targets.ToList())
                        .Set(x => x.FreshnessState, WorkReportStatisticRebuildJobFreshnessStates.Fresh)
                        .Set(x => x.StaleReason, null)
                        .Set(x => x.DiagnosticCode, null)
                        .Set(x => x.StateRevision, nextRevision)
                        .Set(x => x.StateHash, nextStateHash)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId),
                    new FindOneAndUpdateOptions<WorkReportStatisticRebuildJob>
                    {
                        ReturnDocument = ReturnDocument.After
                    },
                    transactionCt);
                return published ?? throw Fail("STALE_PUBLICATION_FENCE");
            },
            ct);
    }

    private async Task<FlowContributionAudit> BuildFlowContributionAuditAsync(
        WorkReportDirectGenerationContext context,
        IReadOnlyCollection<DirectMember> members,
        string receiptId,
        CancellationToken ct)
    {
        var flowMembers = members
            .Where(member =>
                member.Runtime.FlowInstanceId is not null &&
                member.Runtime.FlowContributionOriginVersionId is not null &&
                member.Mapping is not null &&
                string.Equals(
                    member.Runtime.LockedContributionPolicy,
                    DynamicFlowContributionPolicyContract.Include,
                    StringComparison.Ordinal))
            .OrderBy(member => member.Report.Id, StringComparer.Ordinal)
            .ToArray();
        var sources = new List<WorkReportFlowContributionSourceAudit>(flowMembers.Length);
        foreach (var member in flowMembers)
            sources.Add(await LoadFlowContributionSourceAuditAsync(context, member, ct));

        var nonFlowSources = BuildNonFlowContributionSources(context, members);
        var sourceRefs = BuildContributionSourceRefs(sources, nonFlowSources);
        var sourcesByReport = sourceRefs.ToDictionary(
            source => source.SourceReportId,
            StringComparer.Ordinal);
        var targets = new List<WorkReportFlowContributionTargetAudit>();
        var fieldRows = await _ctx.WorkReportFieldStatValues
            .Find(row => row.DirectProjection != null &&
                         row.DirectProjection.GenerationId == context.GenerationId &&
                         !row.IsDeleted)
            .SortBy(row => row.Id)
            .ToListAsync(ct);
        AddContributionTargetsV2(
            targets,
            "work_report_field_stat_values",
            fieldRows,
            row => row.Id,
            row => row.DirectProjection,
            sourcesByReport,
            context,
            receiptId);

        var tableRows = await _ctx.WorkReportTableStatValues
            .Find(row => row.DirectProjection != null &&
                         row.DirectProjection.GenerationId == context.GenerationId &&
                         !row.IsDeleted)
            .SortBy(row => row.Id)
            .ToListAsync(ct);
        AddContributionTargetsV2(
            targets,
            "work_report_table_stat_values",
            tableRows,
            row => row.Id,
            row => row.DirectProjection,
            sourcesByReport,
            context,
            receiptId);

        var labelRows = await _ctx.WorkReportLabelStatValues
            .Find(row => row.DirectProjection != null &&
                         row.DirectProjection.GenerationId == context.GenerationId &&
                         !row.IsDeleted)
            .SortBy(row => row.Id)
            .ToListAsync(ct);
        AddContributionTargetsV2(
            targets,
            "work_report_label_stat_values",
            labelRows,
            row => row.Id,
            row => row.DirectProjection,
            sourcesByReport,
            context,
            receiptId);

        targets = targets
            .OrderBy(target => target.TargetStore, StringComparer.Ordinal)
            .ThenBy(target => target.TargetStatisticId, StringComparer.Ordinal)
            .ToList();
        if (targets.Select(target => target.ContributionId).Distinct(StringComparer.Ordinal).Count() != targets.Count ||
            targets.Select(target => $"{target.TargetStore}\n{target.TargetStatisticId}")
                .Distinct(StringComparer.Ordinal).Count() != targets.Count)
        {
            throw Fail("CONTRIBUTION_TARGET_AMBIGUOUS");
        }

        var ledgerHash = ComputeContributionLedgerV2(
            context.RunId,
            receiptId,
            context.GenerationId,
            context.SourceMembershipSignature,
            sourceRefs,
            targets);
        var reversalBaselineHash = ComputeContributionReversalBaselineV2(
            context.RunId,
            context.GenerationId,
            ledgerHash,
            sourceRefs,
            targets);
        return new FlowContributionAudit(
            sources,
            nonFlowSources,
            targets,
            ledgerHash,
            reversalBaselineHash);
    }

    private async Task<WorkReportFlowContributionSourceAudit>
        LoadFlowContributionSourceAuditAsync(
            WorkReportDirectGenerationContext context,
            DirectMember member,
            CancellationToken ct)
    {
        var binding = member.Mapping ?? throw Fail("FLOW_MAPPING_LINEAGE_MISSING");
        var receipt = await _ctx.DynamicFlowMappingApplyReceipts
            .Find(item =>
                item.Id == binding.ReceiptId &&
                item.TargetReportId == member.Report.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw Fail("FLOW_MAPPING_RECEIPT_MISSING");
        var provenance = await _ctx.DynamicFlowMappingProvenanceRecords
            .Find(item =>
                item.Id == binding.ProvenanceId &&
                item.ReceiptId == binding.ReceiptId &&
                item.TargetReportId == member.Report.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw Fail("FLOW_MAPPING_PROVENANCE_MISSING");

        var mapsCurrentVersion = string.Equals(
            receipt.RuntimePin.FlowVersionId,
            member.Runtime.FlowTemplateVersionId,
            StringComparison.Ordinal);
        var mapsOriginVersion = !string.IsNullOrWhiteSpace(
                                    member.Runtime.FlowContributionOriginVersionId) &&
                                string.Equals(
                                    receipt.RuntimePin.FlowVersionId,
                                    member.Runtime.FlowContributionOriginVersionId,
                                    StringComparison.Ordinal);
        DynamicFlowTemplateVersion? origin = null;
        if (mapsOriginVersion)
        {
            origin = await _ctx.DynamicFlowTemplateVersions
                .Find(version =>
                    version.Id == member.Runtime.FlowContributionOriginVersionId &&
                    version.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                    !version.IsDeleted)
                .FirstOrDefaultAsync(ct);
        }
        var mappingVersionMatches =
            (mapsCurrentVersion &&
             receipt.RuntimePin.FlowVersionNo == member.Assignment.FlowTemplateVersionNo) ||
            (mapsOriginVersion &&
             origin is not null &&
             receipt.RuntimePin.FlowVersionNo == origin.VersionNo &&
             string.Equals(origin.PayloadHash, member.Runtime.FlowPayloadHash, StringComparison.Ordinal));
        if (!mappingVersionMatches ||
            receipt.State is not (
                DynamicFlowMappingApplyStates.Committed or
                DynamicFlowMappingApplyStates.Reconciled) ||
            !string.Equals(provenance.State, DynamicFlowMappingProvenanceStates.Current, StringComparison.Ordinal) ||
            !string.Equals(receipt.WorkId, member.Report.WorkId, StringComparison.Ordinal) ||
            !string.Equals(receipt.TargetAssignmentId, member.Report.WorkAssignmentId, StringComparison.Ordinal) ||
            !string.Equals(provenance.WorkId, member.Report.WorkId, StringComparison.Ordinal) ||
            !string.Equals(provenance.TargetAssignmentId, member.Report.WorkAssignmentId, StringComparison.Ordinal) ||
            !string.Equals(receipt.ProvenanceId, provenance.Id, StringComparison.Ordinal) ||
            !string.Equals(receipt.ProvenanceHash, provenance.ProvenanceHash, StringComparison.Ordinal) ||
            !string.Equals(receipt.RuntimePin.MappingRuleSetHash, provenance.MappingRuleSetHash, StringComparison.Ordinal) ||
            !string.Equals(receipt.RuntimePin.FlowFamilyId, member.Assignment.FlowTemplateId, StringComparison.Ordinal) ||
            !string.Equals(receipt.RuntimePin.FlowPayloadHash, member.Runtime.FlowPayloadHash, StringComparison.Ordinal) ||
            !string.Equals(receipt.RuntimePin.CatalogSemanticHash, member.Runtime.FlowCatalogSemanticHash, StringComparison.Ordinal) ||
            !string.Equals(receipt.RuntimePin.FlowInstanceId, member.Runtime.FlowInstanceId, StringComparison.Ordinal) ||
            receipt.RuntimePin.ExecutionEpoch != member.Runtime.ExecutionEpoch ||
            !string.Equals(receipt.RuntimePin.StepInstanceId, member.Runtime.StepInstanceId, StringComparison.Ordinal) ||
            !string.Equals(receipt.RuntimePin.StepId, member.Assignment.FlowStepId, StringComparison.Ordinal) ||
            !string.Equals(receipt.RuntimePin.BranchId, member.Assignment.FlowBranchId, StringComparison.Ordinal) ||
            receipt.RuntimePin.AttemptNo != member.Assignment.FlowAttemptNo ||
            !string.Equals(binding.ResultPayloadHash, receipt.ResultPayloadHash, StringComparison.Ordinal) ||
            binding.ResultPayloadRevision != receipt.ResultPayloadRevision ||
            !string.Equals(
                member.Runtime.LockedContributionPolicy,
                DynamicFlowContributionPolicyContract.Include,
                StringComparison.Ordinal) ||
            !StatRunCanonicalJson.IsCanonicalSha256(member.Runtime.LockedContributionPolicyHash))
        {
            throw Fail("FLOW_MAPPING_LINEAGE_STALE");
        }

        var sourcePinsHash = DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(
            receipt.SourcePins);
        var sourceMaterial = new
        {
            version = "P9_FLOW_CONTRIBUTION_SOURCE_V1",
            member.Report.Id,
            member.Report.PayloadRevision,
            member.Report.PayloadHash,
            member.Report.LifecycleRevision,
            approvalCommandId = member.ApprovalEntry.CommandId,
            approvalEventKey = member.ApprovalEntry.EntryKey,
            mappingReceiptId = receipt.Id,
            mappingProvenanceId = provenance.Id,
            mappingProvenanceHash = provenance.ProvenanceHash,
            mappingCommandId = receipt.CommandId,
            mappingRequestHash = receipt.RequestHash,
            receipt.PreviewTokenId,
            receipt.PreviewTokenHash,
            receipt.SourceSignatureVersion,
            receipt.SourceSignature,
            sourcePinsHash,
            receipt.ResultSemanticHash,
            receipt.ResultPayloadRevision,
            receipt.ResultPayloadHash,
            receipt.RuntimePin.MappingRuleSetHash,
            receipt.RuntimePin.EvaluatorVersion,
            receipt.RuntimePin.FunctionRegistryVersion,
            receipt.RuntimePin.FunctionRegistryHash,
            mappingFlowVersionId = receipt.RuntimePin.FlowVersionId,
            mappingFlowVersionNo = receipt.RuntimePin.FlowVersionNo,
            mappingFlowPayloadHash = receipt.RuntimePin.FlowPayloadHash,
            flowTemplateVersionId = member.Runtime.FlowTemplateVersionId,
            flowPayloadHash = member.Runtime.FlowPayloadHash,
            flowInstanceId = member.Runtime.FlowInstanceId,
            flowExecutionEpoch = member.Runtime.ExecutionEpoch,
            flowStepInstanceId = member.Runtime.StepInstanceId,
            flowBranchId = member.Assignment.FlowBranchId,
            flowStepId = member.Assignment.FlowStepId,
            flowAttemptNo = member.Assignment.FlowAttemptNo,
            member.Report.WorkId,
            member.Report.WorkAssignmentId,
            member.Report.PeriodInstanceKey,
            context.ConfigVersionId,
            context.ConfigHash,
            membershipSignature = context.SourceMembershipSignature,
            contributionPolicy = member.Runtime.LockedContributionPolicy,
            contributionPolicyHash = member.Runtime.LockedContributionPolicyHash
        };
        var sourceAuditHash = StatRunCanonicalJson.HashObject(sourceMaterial);
        var sourceAuditId = StatRunCanonicalJson.HashText(
            $"P9_FLOW_CONTRIBUTION_SOURCE_AUDIT_V1\n{sourceAuditHash}");
        var reversalIdentity = StatRunCanonicalJson.HashText(
            $"P9_FLOW_CONTRIBUTION_SOURCE_INVERSE_V1\n{sourceAuditId}\n{context.GenerationId}");
        return new WorkReportFlowContributionSourceAudit
        {
            SourceAuditId = sourceAuditId,
            SourceReportId = member.Report.Id,
            SourcePayloadRevision = member.Report.PayloadRevision,
            SourcePayloadHash = member.Report.PayloadHash ?? string.Empty,
            SourceLifecycleRevision = member.Report.LifecycleRevision,
            ApprovalCommandId = member.ApprovalEntry.CommandId,
            ApprovalEventKey = member.ApprovalEntry.EntryKey,
            MappingReceiptId = receipt.Id,
            MappingProvenanceId = provenance.Id,
            MappingProvenanceHash = provenance.ProvenanceHash,
            MappingCommandId = receipt.CommandId,
            MappingRequestHash = receipt.RequestHash,
            PreviewTokenId = receipt.PreviewTokenId,
            PreviewTokenHash = receipt.PreviewTokenHash,
            MappingSourceSignatureVersion = receipt.SourceSignatureVersion,
            MappingSourceSignature = receipt.SourceSignature,
            MappingSourcePinsHash = sourcePinsHash,
            MappingResultSemanticHash = receipt.ResultSemanticHash,
            MappingResultPayloadRevision = receipt.ResultPayloadRevision,
            MappingResultPayloadHash = receipt.ResultPayloadHash,
            MappingRuleSetHash = receipt.RuntimePin.MappingRuleSetHash,
            MappingEvaluatorVersion = receipt.RuntimePin.EvaluatorVersion,
            MappingFunctionRegistryVersion = receipt.RuntimePin.FunctionRegistryVersion,
            MappingFunctionRegistryHash = receipt.RuntimePin.FunctionRegistryHash,
            MappingFlowVersionId = receipt.RuntimePin.FlowVersionId,
            MappingFlowVersionNo = receipt.RuntimePin.FlowVersionNo,
            MappingFlowPayloadHash = receipt.RuntimePin.FlowPayloadHash,
            FlowTemplateVersionId = member.Runtime.FlowTemplateVersionId!,
            FlowPayloadHash = member.Runtime.FlowPayloadHash!,
            FlowInstanceId = member.Runtime.FlowInstanceId!,
            FlowExecutionEpoch = member.Runtime.ExecutionEpoch!.Value,
            FlowStepInstanceId = member.Runtime.StepInstanceId!,
            FlowBranchId = member.Assignment.FlowBranchId!,
            FlowStepId = member.Assignment.FlowStepId!,
            FlowAttemptNo = member.Assignment.FlowAttemptNo!.Value,
            WorkId = member.Report.WorkId,
            WorkAssignmentId = member.Report.WorkAssignmentId,
            PeriodInstanceKey = member.Report.PeriodInstanceKey,
            ConfigVersionId = context.ConfigVersionId,
            ConfigHash = context.ConfigHash,
            MembershipSignature = context.SourceMembershipSignature,
            ContributionPolicy = member.Runtime.LockedContributionPolicy!,
            ContributionPolicyHash = member.Runtime.LockedContributionPolicyHash!,
            ReversalIdentity = reversalIdentity,
            SourceAuditHash = sourceAuditHash
        };
    }

    private static void ValidateStoredFlowContributionAudit(
        WorkReportStatisticRebuildJob job,
        FlowContributionAudit observed)
    {
        var storedSourceHashes = job.FlowContributionSources
            .OrderBy(source => source.SourceReportId, StringComparer.Ordinal)
            .Select(source => source.SourceAuditHash)
            .ToArray();
        var observedSourceHashes = observed.Sources
            .OrderBy(source => source.SourceReportId, StringComparer.Ordinal)
            .Select(source => source.SourceAuditHash)
            .ToArray();
        var storedNonFlowSourceHashes = job.NonFlowContributionSources
            .OrderBy(source => source.SourceReportId, StringComparer.Ordinal)
            .Select(source => source.SourceAuditHash)
            .ToArray();
        var observedNonFlowSourceHashes = observed.NonFlowSources
            .OrderBy(source => source.SourceReportId, StringComparer.Ordinal)
            .Select(source => source.SourceAuditHash)
            .ToArray();
        var storedTargetHashes = job.FlowContributionTargets
            .OrderBy(target => target.TargetStore, StringComparer.Ordinal)
            .ThenBy(target => target.TargetStatisticId, StringComparer.Ordinal)
            .Select(target => target.LedgerEntryHash)
            .ToArray();
        var observedTargetHashes = observed.Targets
            .OrderBy(target => target.TargetStore, StringComparer.Ordinal)
            .ThenBy(target => target.TargetStatisticId, StringComparer.Ordinal)
            .Select(target => target.LedgerEntryHash)
            .ToArray();
        if (!string.Equals(job.FlowContributionLedgerHash, observed.LedgerHash, StringComparison.Ordinal) ||
            !string.Equals(job.FlowContributionReversalBaselineHash, observed.ReversalBaselineHash, StringComparison.Ordinal) ||
            job.FlowContributionSourceCount != observed.Sources.Count ||
            job.NonFlowContributionSourceCount != observed.NonFlowSources.Count ||
            job.FlowContributionTargetCount != observed.Targets.Count ||
            !storedSourceHashes.SequenceEqual(observedSourceHashes, StringComparer.Ordinal) ||
            !storedNonFlowSourceHashes.SequenceEqual(
                observedNonFlowSourceHashes, StringComparer.Ordinal) ||
            !storedTargetHashes.SequenceEqual(observedTargetHashes, StringComparer.Ordinal))
        {
            throw Fail("FLOW_CONTRIBUTION_REPLAY_MISMATCH");
        }
    }

    private static void ValidatePersistedFlowContributionAudit(
        WorkReportStatisticRebuildJob job)
    {
        if (string.Equals(job.FlowContributionOperationVersion,
                ContributionOperationVersion, StringComparison.Ordinal))
        {
            ValidatePersistedContributionAuditV2(job);
            return;
        }
        var sources = job.FlowContributionSources ?? [];
        var nonFlowSources = job.NonFlowContributionSources ?? [];
        var targets = job.FlowContributionTargets ?? [];
        var isLegacyV1EmptyLedger =
            sources.Count == 0 &&
            nonFlowSources.Count == 0 &&
            targets.Count == 0 &&
            job.FlowContributionSourceCount == 0 &&
            job.NonFlowContributionSourceCount == 0 &&
            job.FlowContributionTargetCount == 0 &&
            string.IsNullOrWhiteSpace(job.FlowContributionOperationVersion) &&
            string.IsNullOrWhiteSpace(job.FlowContributionLedgerHash) &&
            string.IsNullOrWhiteSpace(job.FlowContributionReversalBaselineHash) &&
            job.ReversalAudit is null;
        if (isLegacyV1EmptyLedger)
            return;

        if (!ObjectId.TryParse(job.Id, out _) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.ReceiptId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.GenerationId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.SourceMembershipSignature) ||
            !string.Equals(
                job.FlowContributionOperationVersion,
                FlowContributionOperationVersion,
                StringComparison.Ordinal) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.FlowContributionLedgerHash) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.FlowContributionReversalBaselineHash) ||
            job.FlowContributionSourceCount != sources.Count ||
            job.NonFlowContributionSourceCount != 0 ||
            nonFlowSources.Count != 0 ||
            job.FlowContributionTargetCount != targets.Count)
        {
            throw Fail("PERSISTED_FLOW_CONTRIBUTION_INVALID");
        }

        var orderedSourceIds = sources
            .OrderBy(source => source.SourceReportId, StringComparer.Ordinal)
            .Select(source => source.SourceReportId)
            .ToArray();
        if (!sources.Select(source => source.SourceReportId)
                .SequenceEqual(orderedSourceIds, StringComparer.Ordinal) ||
            sources.Select(source => source.SourceReportId)
                .Distinct(StringComparer.Ordinal).Count() != sources.Count ||
            sources.Select(source => source.SourceAuditId)
                .Distinct(StringComparer.Ordinal).Count() != sources.Count ||
            sources.Select(source => source.ReversalIdentity)
                .Distinct(StringComparer.Ordinal).Count() != sources.Count)
        {
            throw Fail("PERSISTED_FLOW_CONTRIBUTION_INVALID");
        }

        foreach (var source in sources)
            ValidatePersistedFlowContributionSource(job, source);

        ValidatePersistedFlowContributionTargets(job, sources, targets);

        var ledgerHash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_FLOW_CONTRIBUTION_LEDGER_V1",
            RunId = job.Id,
            receiptId = job.ReceiptId,
            GenerationId = job.GenerationId,
            SourceMembershipSignature = job.SourceMembershipSignature,
            operationVersion = FlowContributionOperationVersion,
            sources = sources.Select(source => new
            {
                source.SourceAuditId,
                source.SourceAuditHash,
                source.ReversalIdentity
            }).ToArray(),
            targets = targets.Select(target => new
            {
                target.ContributionId,
                target.LedgerEntryHash,
                target.ReversalIdentity
            }).ToArray()
        });
        var reversalBaselineHash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_FLOW_CONTRIBUTION_REVERSAL_BASELINE_V1",
            RunId = job.Id,
            GenerationId = job.GenerationId,
            ledgerHash,
            sourceReversals = sources.Select(source => source.ReversalIdentity).ToArray(),
            targetReversals = targets.Select(target => target.ReversalIdentity).ToArray()
        });
        if (!string.Equals(job.FlowContributionLedgerHash, ledgerHash, StringComparison.Ordinal) ||
            !string.Equals(
                job.FlowContributionReversalBaselineHash,
                reversalBaselineHash,
                StringComparison.Ordinal))
        {
            throw Fail("PERSISTED_FLOW_CONTRIBUTION_INVALID");
        }

        ValidatePersistedReversalAudit(job);
    }

    private static void ValidatePersistedFlowContributionSource(
        WorkReportStatisticRebuildJob job,
        WorkReportFlowContributionSourceAudit source)
    {
        if (!ObjectId.TryParse(source.SourceReportId, out _) ||
            source.SourcePayloadRevision < 1 ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.SourcePayloadHash) ||
            source.SourceLifecycleRevision < 1 ||
            string.IsNullOrWhiteSpace(source.ApprovalCommandId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.ApprovalEventKey) ||
            !ObjectId.TryParse(source.MappingReceiptId, out _) ||
            !ObjectId.TryParse(source.MappingProvenanceId, out _) ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.MappingProvenanceHash) ||
            string.IsNullOrWhiteSpace(source.MappingCommandId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.MappingRequestHash) ||
            string.IsNullOrWhiteSpace(source.PreviewTokenId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.PreviewTokenHash) ||
            string.IsNullOrWhiteSpace(source.MappingSourceSignatureVersion) ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.MappingSourceSignature) ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.MappingSourcePinsHash) ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.MappingResultSemanticHash) ||
            source.MappingResultPayloadRevision < 1 ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.MappingResultPayloadHash) ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.MappingRuleSetHash) ||
            string.IsNullOrWhiteSpace(source.MappingEvaluatorVersion) ||
            string.IsNullOrWhiteSpace(source.MappingFunctionRegistryVersion) ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.MappingFunctionRegistryHash) ||
            !ObjectId.TryParse(source.MappingFlowVersionId, out _) ||
            source.MappingFlowVersionNo < 1 ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.MappingFlowPayloadHash) ||
            !ObjectId.TryParse(source.FlowTemplateVersionId, out _) ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.FlowPayloadHash) ||
            !ObjectId.TryParse(source.FlowInstanceId, out _) ||
            source.FlowExecutionEpoch < 1 ||
            !ObjectId.TryParse(source.FlowStepInstanceId, out _) ||
            !ObjectId.TryParse(source.FlowBranchId, out _) ||
            string.IsNullOrWhiteSpace(source.FlowStepId) ||
            source.FlowAttemptNo < 1 ||
            !ObjectId.TryParse(source.WorkId, out _) ||
            !ObjectId.TryParse(source.WorkAssignmentId, out _) ||
            string.IsNullOrWhiteSpace(source.PeriodInstanceKey) ||
            !ObjectId.TryParse(source.ConfigVersionId, out _) ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.ConfigHash) ||
            !string.Equals(
                source.MembershipSignature,
                job.SourceMembershipSignature,
                StringComparison.Ordinal) ||
            !string.Equals(
                source.ContributionPolicy,
                DynamicFlowContributionPolicyContract.Include,
                StringComparison.Ordinal) ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.ContributionPolicyHash))
        {
            throw Fail("PERSISTED_FLOW_CONTRIBUTION_INVALID");
        }

        var sourceAuditHash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_FLOW_CONTRIBUTION_SOURCE_V1",
            Id = source.SourceReportId,
            PayloadRevision = source.SourcePayloadRevision,
            PayloadHash = source.SourcePayloadHash,
            LifecycleRevision = source.SourceLifecycleRevision,
            approvalCommandId = source.ApprovalCommandId,
            approvalEventKey = source.ApprovalEventKey,
            mappingReceiptId = source.MappingReceiptId,
            mappingProvenanceId = source.MappingProvenanceId,
            mappingProvenanceHash = source.MappingProvenanceHash,
            mappingCommandId = source.MappingCommandId,
            mappingRequestHash = source.MappingRequestHash,
            PreviewTokenId = source.PreviewTokenId,
            PreviewTokenHash = source.PreviewTokenHash,
            SourceSignatureVersion = source.MappingSourceSignatureVersion,
            SourceSignature = source.MappingSourceSignature,
            sourcePinsHash = source.MappingSourcePinsHash,
            ResultSemanticHash = source.MappingResultSemanticHash,
            ResultPayloadRevision = source.MappingResultPayloadRevision,
            ResultPayloadHash = source.MappingResultPayloadHash,
            MappingRuleSetHash = source.MappingRuleSetHash,
            EvaluatorVersion = source.MappingEvaluatorVersion,
            FunctionRegistryVersion = source.MappingFunctionRegistryVersion,
            FunctionRegistryHash = source.MappingFunctionRegistryHash,
            mappingFlowVersionId = source.MappingFlowVersionId,
            mappingFlowVersionNo = source.MappingFlowVersionNo,
            mappingFlowPayloadHash = source.MappingFlowPayloadHash,
            flowTemplateVersionId = source.FlowTemplateVersionId,
            flowPayloadHash = source.FlowPayloadHash,
            flowInstanceId = source.FlowInstanceId,
            flowExecutionEpoch = source.FlowExecutionEpoch,
            flowStepInstanceId = source.FlowStepInstanceId,
            flowBranchId = source.FlowBranchId,
            flowStepId = source.FlowStepId,
            flowAttemptNo = source.FlowAttemptNo,
            WorkId = source.WorkId,
            WorkAssignmentId = source.WorkAssignmentId,
            PeriodInstanceKey = source.PeriodInstanceKey,
            ConfigVersionId = source.ConfigVersionId,
            ConfigHash = source.ConfigHash,
            membershipSignature = source.MembershipSignature,
            contributionPolicy = source.ContributionPolicy,
            contributionPolicyHash = source.ContributionPolicyHash
        });
        var sourceAuditId = StatRunCanonicalJson.HashText(
            $"P9_FLOW_CONTRIBUTION_SOURCE_AUDIT_V1\n{sourceAuditHash}");
        var reversalIdentity = StatRunCanonicalJson.HashText(
            $"P9_FLOW_CONTRIBUTION_SOURCE_INVERSE_V1\n{sourceAuditId}\n{job.GenerationId}");
        if (!string.Equals(source.SourceAuditHash, sourceAuditHash, StringComparison.Ordinal) ||
            !string.Equals(source.SourceAuditId, sourceAuditId, StringComparison.Ordinal) ||
            !string.Equals(source.ReversalIdentity, reversalIdentity, StringComparison.Ordinal))
        {
            throw Fail("PERSISTED_FLOW_CONTRIBUTION_INVALID");
        }
    }

    private static void ValidatePersistedFlowContributionTargets(
        WorkReportStatisticRebuildJob job,
        IReadOnlyCollection<WorkReportFlowContributionSourceAudit> sources,
        IReadOnlyCollection<WorkReportFlowContributionTargetAudit> targets)
    {
        var sourcesByAuditId = sources.ToDictionary(
            source => source.SourceAuditId,
            StringComparer.Ordinal);
        var orderedTargetKeys = targets
            .OrderBy(target => target.TargetStore, StringComparer.Ordinal)
            .ThenBy(target => target.TargetStatisticId, StringComparer.Ordinal)
            .Select(target => $"{target.TargetStore}\n{target.TargetStatisticId}")
            .ToArray();
        var targetKeys = targets
            .Select(target => $"{target.TargetStore}\n{target.TargetStatisticId}")
            .ToArray();
        if (!targetKeys.SequenceEqual(orderedTargetKeys, StringComparer.Ordinal) ||
            targetKeys.Distinct(StringComparer.Ordinal).Count() != targets.Count ||
            targets.Select(target => target.ContributionId)
                .Distinct(StringComparer.Ordinal).Count() != targets.Count ||
            targets.Select(target => target.ReversalIdentity)
                .Distinct(StringComparer.Ordinal).Count() != targets.Count)
        {
            throw Fail("PERSISTED_FLOW_CONTRIBUTION_INVALID");
        }

        foreach (var target in targets)
        {
            if (!sourcesByAuditId.TryGetValue(target.SourceAuditId, out var source) ||
                !string.Equals(target.SourceReportId, source.SourceReportId, StringComparison.Ordinal) ||
                target.TargetStore is not (
                    "work_report_field_stat_values" or
                    "work_report_table_stat_values" or
                    "work_report_label_stat_values") ||
                !ObjectId.TryParse(target.TargetStatisticId, out _) ||
                !StatRunCanonicalJson.IsCanonicalSha256(target.TargetIdentityHash) ||
                !string.Equals(
                    target.Operation,
                    DynamicFlowContributionPolicyContract.Include,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    target.OperationVersion,
                    FlowContributionOperationVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(target.RunId, job.Id, StringComparison.Ordinal) ||
                !string.Equals(target.ReceiptId, job.ReceiptId, StringComparison.Ordinal) ||
                !string.Equals(target.GenerationId, job.GenerationId, StringComparison.Ordinal) ||
                !string.Equals(
                    target.State,
                    WorkReportFlowContributionStates.Applied,
                    StringComparison.Ordinal))
            {
                throw Fail("PERSISTED_FLOW_CONTRIBUTION_INVALID");
            }

            var contributionId = StatRunCanonicalJson.HashObject(new
            {
                version = "P9_FLOW_CONTRIBUTION_TARGET_V1",
                source.SourceAuditId,
                source.SourceAuditHash,
                store = target.TargetStore,
                targetId = target.TargetStatisticId,
                targetIdentityHash = target.TargetIdentityHash,
                operation = DynamicFlowContributionPolicyContract.Include,
                operationVersion = FlowContributionOperationVersion,
                RunId = job.Id,
                receiptId = job.ReceiptId,
                GenerationId = job.GenerationId,
                SourceMembershipSignature = job.SourceMembershipSignature
            });
            var reversalIdentity = StatRunCanonicalJson.HashText(
                $"P9_FLOW_CONTRIBUTION_TARGET_INVERSE_V1\n{contributionId}");
            var ledgerEntryHash = StatRunCanonicalJson.HashObject(new
            {
                version = "P9_FLOW_CONTRIBUTION_LEDGER_ENTRY_V1",
                contributionId,
                source.SourceAuditId,
                source.SourceReportId,
                store = target.TargetStore,
                targetId = target.TargetStatisticId,
                targetIdentityHash = target.TargetIdentityHash,
                operation = DynamicFlowContributionPolicyContract.Include,
                operationVersion = FlowContributionOperationVersion,
                RunId = job.Id,
                receiptId = job.ReceiptId,
                GenerationId = job.GenerationId,
                reversalIdentity,
                state = WorkReportFlowContributionStates.Applied
            });
            if (!string.Equals(target.ContributionId, contributionId, StringComparison.Ordinal) ||
                !string.Equals(target.ReversalIdentity, reversalIdentity, StringComparison.Ordinal) ||
                !string.Equals(target.LedgerEntryHash, ledgerEntryHash, StringComparison.Ordinal))
            {
                throw Fail("PERSISTED_FLOW_CONTRIBUTION_INVALID");
            }
        }
    }

    private static void ValidatePersistedReversalAudit(
        WorkReportStatisticRebuildJob job)
    {
        var audit = job.ReversalAudit;
        if (audit is null)
            return;

        if (audit.Operation is not (
                "REVIEW_RECALL_APPROVED" or
                "REVIEW_RETURN" or
                "REVIEW_DEACTIVATE_REPORT" or
                "WITHDRAW" or
                "AUTO_AGGREGATE_REVIEW_INVALIDATED" or
                "REVIEW_REACTIVATE_REPORT") ||
            !string.Equals(audit.EventKey, job.SourceLifecycleEventKey, StringComparison.Ordinal) ||
            !string.Equals(audit.SourceReportId, job.SourceReportId, StringComparison.Ordinal) ||
            audit.SourceLifecycleRevision != job.SourceLifecycleRevision ||
            !string.Equals(audit.ReceiptId, job.ReceiptId, StringComparison.Ordinal) ||
            !ObjectId.TryParse(audit.PriorRunId, out _) ||
            string.Equals(audit.PriorRunId, job.Id, StringComparison.Ordinal) ||
            !StatRunCanonicalJson.IsCanonicalSha256(audit.PriorGenerationId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(audit.PriorGenerationHash) ||
            (audit.PriorLedgerHash is not null &&
             !StatRunCanonicalJson.IsCanonicalSha256(audit.PriorLedgerHash)) ||
            (audit.PriorReversalBaselineHash is not null &&
             !StatRunCanonicalJson.IsCanonicalSha256(audit.PriorReversalBaselineHash)) ||
            audit.PriorSourceCount != (audit.PriorSourceAuditHashes ?? []).Count ||
            audit.PriorTargetCount != (audit.PriorTargetLedgerHashes ?? []).Count ||
            (audit.PriorSourceAuditHashes ?? []).Any(hash =>
                !StatRunCanonicalJson.IsCanonicalSha256(hash)) ||
            (audit.PriorTargetLedgerHashes ?? []).Any(hash =>
                !StatRunCanonicalJson.IsCanonicalSha256(hash)))
        {
            throw Fail("PERSISTED_REVERSAL_AUDIT_INVALID");
        }

        var auditHash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_DIRECT_REVERSAL_AUDIT_V1",
            RunId = job.Id,
            GenerationId = job.GenerationId,
            audit.Operation,
            audit.EventKey,
            audit.SourceReportId,
            audit.SourceLifecycleRevision,
            audit.ReceiptId,
            audit.PriorRunId,
            audit.PriorGenerationId,
            audit.PriorGenerationHash,
            audit.PriorLedgerHash,
            audit.PriorReversalBaselineHash,
            audit.PriorSourceCount,
            audit.PriorTargetCount,
            audit.PriorSourceAuditHashes,
            audit.PriorTargetLedgerHashes
        });
        if (!string.Equals(audit.AuditHash, auditHash, StringComparison.Ordinal))
            throw Fail("PERSISTED_REVERSAL_AUDIT_INVALID");
    }

    private static WorkReportStatisticReversalAudit? BuildReversalAudit(
        WorkReportDirectGenerationContext context,
        DirectSource source,
        string receiptId,
        ReversalContext? reversal)
    {
        if (reversal is null)
            return null;

        var prior = reversal.PriorPublication;
        var sourceHashes = ContributionSourceHashesForReport(
            prior,
            source.Report.Id).ToList();
        var targetHashes = (prior.FlowContributionTargets ?? [])
            .Where(item => string.Equals(
                item.SourceReportId,
                source.Report.Id,
                StringComparison.Ordinal))
            .OrderBy(item => item.TargetStore, StringComparer.Ordinal)
            .ThenBy(item => item.TargetStatisticId, StringComparer.Ordinal)
            .Select(item => item.LedgerEntryHash)
            .ToList();
        var audit = new WorkReportStatisticReversalAudit
        {
            Operation = source.Entry.Operation,
            EventKey = source.Entry.EntryKey,
            SourceReportId = source.Report.Id,
            SourceLifecycleRevision = source.Report.LifecycleRevision,
            ReceiptId = receiptId,
            PriorRunId = prior.Id,
            PriorGenerationId = prior.GenerationId!,
            PriorGenerationHash = prior.GenerationHash!,
            PriorLedgerHash = prior.FlowContributionLedgerHash,
            PriorReversalBaselineHash = prior.FlowContributionReversalBaselineHash,
            PriorSourceCount = sourceHashes.Count,
            PriorTargetCount = targetHashes.Count,
            PriorSourceAuditHashes = sourceHashes,
            PriorTargetLedgerHashes = targetHashes
        };
        audit.AuditHash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_DIRECT_REVERSAL_AUDIT_V1",
            context.RunId,
            context.GenerationId,
            audit.Operation,
            audit.EventKey,
            audit.SourceReportId,
            audit.SourceLifecycleRevision,
            audit.ReceiptId,
            audit.PriorRunId,
            audit.PriorGenerationId,
            audit.PriorGenerationHash,
            audit.PriorLedgerHash,
            audit.PriorReversalBaselineHash,
            audit.PriorSourceCount,
            audit.PriorTargetCount,
            audit.PriorSourceAuditHashes,
            audit.PriorTargetLedgerHashes
        });
        return audit;
    }

    private static bool SameReversalAudit(
        WorkReportStatisticReversalAudit? left,
        WorkReportStatisticReversalAudit? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        return string.Equals(left.AuditHash, right.AuditHash, StringComparison.Ordinal) &&
               string.Equals(left.Operation, right.Operation, StringComparison.Ordinal) &&
               string.Equals(left.EventKey, right.EventKey, StringComparison.Ordinal) &&
               string.Equals(left.SourceReportId, right.SourceReportId, StringComparison.Ordinal) &&
               left.SourceLifecycleRevision == right.SourceLifecycleRevision &&
               string.Equals(left.ReceiptId, right.ReceiptId, StringComparison.Ordinal) &&
               string.Equals(left.PriorRunId, right.PriorRunId, StringComparison.Ordinal) &&
               string.Equals(left.PriorGenerationId, right.PriorGenerationId, StringComparison.Ordinal) &&
               string.Equals(left.PriorGenerationHash, right.PriorGenerationHash, StringComparison.Ordinal) &&
               string.Equals(left.PriorLedgerHash, right.PriorLedgerHash, StringComparison.Ordinal) &&
               string.Equals(left.PriorReversalBaselineHash, right.PriorReversalBaselineHash, StringComparison.Ordinal) &&
               left.PriorSourceCount == right.PriorSourceCount &&
               left.PriorTargetCount == right.PriorTargetCount &&
               (left.PriorSourceAuditHashes ?? []).SequenceEqual(
                   right.PriorSourceAuditHashes ?? [],
                   StringComparer.Ordinal) &&
               (left.PriorTargetLedgerHashes ?? []).SequenceEqual(
                   right.PriorTargetLedgerHashes ?? [],
                   StringComparer.Ordinal);
    }

    private async Task<IReadOnlyList<WorkReportDirectStoreDigest>> ComputeAndValidateDigestsAsync(
        WorkReportDirectGenerationContext context,
        IReadOnlyCollection<DirectMember> members,
        CancellationToken ct)
    {
        var fieldValues = await _ctx.WorkReportFieldStatValues
            .Find(x => x.DirectProjection != null &&
                       x.DirectProjection.GenerationId == context.GenerationId &&
                       !x.IsDeleted)
            .SortBy(x => x.Id)
            .ToListAsync(ct);
        var fieldAggregates = await _ctx.WorkReportFieldStatAggregates
            .Find(x => x.DirectProjection != null &&
                       x.DirectProjection.GenerationId == context.GenerationId &&
                       !x.IsDeleted)
            .SortBy(x => x.Id)
            .ToListAsync(ct);
        var tableValues = await _ctx.WorkReportTableStatValues
            .Find(x => x.DirectProjection != null &&
                       x.DirectProjection.GenerationId == context.GenerationId &&
                       !x.IsDeleted)
            .SortBy(x => x.Id)
            .ToListAsync(ct);
        var tableAggregates = await _ctx.WorkReportTableStatAggregates
            .Find(x => x.DirectProjection != null &&
                       x.DirectProjection.GenerationId == context.GenerationId &&
                       !x.IsDeleted)
            .SortBy(x => x.Id)
            .ToListAsync(ct);
        var labelValues = await _ctx.WorkReportLabelStatValues
            .Find(x => x.DirectProjection != null &&
                       x.DirectProjection.GenerationId == context.GenerationId &&
                       !x.IsDeleted)
            .SortBy(x => x.Id)
            .ToListAsync(ct);
        var labelAggregates = await _ctx.WorkReportLabelStatAggregates
            .Find(x => x.DirectProjection != null &&
                       x.DirectProjection.GenerationId == context.GenerationId &&
                       !x.IsDeleted)
            .SortBy(x => x.Id)
            .ToListAsync(ct);

        EnsurePins(fieldValues.Select(x => x.DirectProjection), context, members);
        EnsurePins(fieldAggregates.Select(x => x.DirectProjection), context, members);
        EnsurePins(tableValues.Select(x => x.DirectProjection), context, members);
        EnsurePins(tableAggregates.Select(x => x.DirectProjection), context, members);
        EnsurePins(labelValues.Select(x => x.DirectProjection), context, members);
        EnsurePins(labelAggregates.Select(x => x.DirectProjection), context, members);

        return new[]
        {
            Digest("work_report_field_stat_values", fieldValues),
            Digest("work_report_field_stat_aggregates", fieldAggregates),
            Digest("work_report_table_stat_values", tableValues),
            Digest("work_report_table_stat_aggregates", tableAggregates),
            Digest("work_report_label_stat_values", labelValues),
            Digest("work_report_label_stat_aggregates", labelAggregates)
        };
    }

    private static WorkReportDirectStoreDigest Digest<T>(
        string store,
        IEnumerable<T> rows)
    {
        var material = rows
            .Select(CanonicalPersistedRow)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.CanonicalJson)
            .ToArray();
        return new WorkReportDirectStoreDigest
        {
            Store = store,
            RowCount = material.LongLength,
            Sha256 = StatRunCanonicalJson.HashText(
                $"P9_DIRECT_STORE_ROWS_V2\n{string.Join("\n", material)}")
        };
    }

    private static (string Id, string CanonicalJson) CanonicalPersistedRow<T>(T row)
    {
        var document = row?.ToBsonDocument()
                       ?? throw Fail("STAGED_ROW_NULL");
        foreach (var operationalField in new[]
                 {
                     "createdAtUtc",
                     "updatedAtUtc",
                     "createdByUserId",
                     "updatedByUserId"
                 })
        {
            document.Remove(operationalField);
        }
        var id = document.TryGetValue("_id", out var idValue)
            ? idValue.ToString()!
            : throw Fail("STAGED_ROW_ID_MISSING");
        var canonical = CanonicalizeBson(document).AsBsonDocument.ToJson(
            new MongoDB.Bson.IO.JsonWriterSettings
            {
                OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson,
                Indent = false
            });
        return (id, canonical);
    }

    private static BsonValue CanonicalizeBson(BsonValue value)
    {
        if (value.IsBsonDocument)
        {
            return new BsonDocument(value.AsBsonDocument.Elements
                .OrderBy(element => element.Name, StringComparer.Ordinal)
                .Select(element => new BsonElement(
                    element.Name,
                    CanonicalizeBson(element.Value))));
        }
        if (value.IsBsonArray)
            return new BsonArray(value.AsBsonArray.Select(CanonicalizeBson));
        return value;
    }

    private static void EnsurePins(
        IEnumerable<WorkReportDirectProjectionPin?> pins,
        WorkReportDirectGenerationContext context,
        IReadOnlyCollection<DirectMember> members)
    {
        var captured = members.ToDictionary(item => item.Report.Id, StringComparer.Ordinal);
        if (pins.Any(pin =>
                pin is null ||
                !captured.TryGetValue(pin.SourceReportId, out var member) ||
                !string.Equals(pin.RunId, context.RunId, StringComparison.Ordinal) ||
                !string.Equals(pin.GenerationId, context.GenerationId, StringComparison.Ordinal) ||
                !string.Equals(pin.LifecycleEventKey, context.LifecycleEventKey, StringComparison.Ordinal) ||
                pin.DirectSourceRevision != context.DirectSourceRevision ||
                !string.Equals(pin.SourceReportId, member.Report.Id, StringComparison.Ordinal) ||
                pin.SourcePayloadRevision != member.Report.PayloadRevision ||
                !string.Equals(pin.SourcePayloadHash, member.Report.PayloadHash, StringComparison.Ordinal) ||
                pin.SourceLifecycleRevision != member.Report.LifecycleRevision ||
                !string.Equals(pin.DynamicFormFamilyId, context.DynamicFormFamilyId, StringComparison.Ordinal) ||
                !string.Equals(pin.DynamicFormTemplateId, context.DynamicFormTemplateId, StringComparison.Ordinal) ||
                pin.DynamicFormVersionNo != context.DynamicFormVersionNo ||
                !string.Equals(pin.DynamicFormSchemaHash, context.DynamicFormSchemaHash, StringComparison.Ordinal) ||
                !string.Equals(pin.ConfigId, context.ConfigId, StringComparison.Ordinal) ||
                !string.Equals(pin.ConfigVersionId, context.ConfigVersionId, StringComparison.Ordinal) ||
                pin.ConfigVersionNo != context.ConfigVersionNo ||
                pin.ConfigRevision != context.ConfigRevision ||
                !string.Equals(pin.ConfigHash, context.ConfigHash, StringComparison.Ordinal) ||
                !string.Equals(pin.CandidateChainId, context.CandidateChainId, StringComparison.Ordinal) ||
                !string.Equals(pin.CatalogVersion, context.CatalogVersion, StringComparison.Ordinal) ||
                !string.Equals(pin.CatalogRawSha256, context.CatalogRawSha256, StringComparison.Ordinal) ||
                !string.Equals(pin.CatalogSemanticSha256, context.CatalogSemanticSha256, StringComparison.Ordinal) ||
                !string.Equals(pin.SchemaRawSha256, context.SchemaRawSha256, StringComparison.Ordinal) ||
                !string.Equals(pin.SchemaSemanticSha256, context.SchemaSemanticSha256, StringComparison.Ordinal) ||
                !string.Equals(pin.StageLockSha256, context.StageLockSha256, StringComparison.Ordinal) ||
                !string.Equals(pin.SourceMembershipSignature, context.SourceMembershipSignature, StringComparison.Ordinal) ||
                !SameUtcMillisecond(pin.ComputedAtUtc, context.ComputedAtUtc)))
        {
            throw Fail("STAGED_PIN_MISMATCH");
        }
    }

    private async Task LinkPublishedAsync(
        string reportId,
        string eventKey,
        WorkReportStatisticRebuildJob job,
        CancellationToken ct)
    {
        if (!StatRunCanonicalJson.IsCanonicalSha256(job.GenerationId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.GenerationHash) ||
            !job.PublishedAtUtc.HasValue ||
            !string.Equals(job.SourceReportId, reportId, StringComparison.Ordinal) ||
            !string.Equals(job.SourceLifecycleEventKey, eventKey, StringComparison.Ordinal))
        {
            throw Fail("PUBLISHED_JOB_INVALID");
        }
        await LinkAsync(
            reportId,
            eventKey,
            StatRunDirectProjectionStates.Published,
            "PUBLISHED",
            job.Id,
            job.GenerationId,
            job.GenerationHash,
            job.PublishedAtUtc.Value,
            ct);
    }

    private async Task<StatRunDirectProjectionResult> LinkZeroWriteAsync(
        string reportId,
        string eventKey,
        string reason,
        CancellationToken ct)
    {
        var completed = await FindVerifiedCompletedPublicationAsync(
            reportId,
            eventKey,
            ct);
        if (completed is not null)
        {
            await LinkPublishedAsync(reportId, eventKey, completed, ct);
            return new StatRunDirectProjectionResult(
                StatRunDirectProjectionStates.Published,
                "PUBLISHED_COMPLETED_RECOVERY",
                completed.Id,
                completed.GenerationId,
                completed.GenerationHash,
                true);
        }

        var linked = await LinkAsync(
            reportId,
            eventKey,
            StatRunDirectProjectionStates.ZeroWrite,
            reason,
            null,
            null,
            null,
            DateTime.UtcNow,
            ct);
        if (string.Equals(
                linked.DirectProjectionState,
                StatRunDirectProjectionStates.Published,
                StringComparison.Ordinal))
        {
            return new StatRunDirectProjectionResult(
                StatRunDirectProjectionStates.Published,
                "PUBLISHED_MONOTONIC_REPLAY",
                linked.DirectProjectionRunId,
                linked.DirectProjectionGenerationId,
                linked.DirectProjectionGenerationHash,
                true);
        }

        // Close the lookup/link race: if publication committed while ZERO_WRITE was
        // being linked, verify its immutable generation and monotonically upgrade.
        completed = await FindVerifiedCompletedPublicationAsync(
            reportId,
            eventKey,
            ct);
        if (completed is not null)
        {
            await LinkPublishedAsync(reportId, eventKey, completed, ct);
            return new StatRunDirectProjectionResult(
                StatRunDirectProjectionStates.Published,
                "PUBLISHED_CONCURRENT_RECOVERY",
                completed.Id,
                completed.GenerationId,
                completed.GenerationHash,
                true);
        }

        return new StatRunDirectProjectionResult(
            StatRunDirectProjectionStates.ZeroWrite,
            linked.DirectProjectionReason ?? reason,
            null,
            null,
            null,
            !string.Equals(linked.DirectProjectionReason, reason, StringComparison.Ordinal));
    }

    private async Task<WorkReportStatisticRebuildJob?> FindVerifiedCompletedPublicationAsync(
        string reportId,
        string eventKey,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var jobs = await _ctx.WorkReportStatisticRebuildJobs
            .Find(
                fb.Eq(x => x.SourceReportId, reportId) &
                fb.Eq(x => x.SourceLifecycleEventKey, eventKey) &
                fb.Eq(x => x.DirectProjectionIdentityVersion, null) &
                fb.Eq(x => x.DirectProjectionIdentityKey, null) &
                fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.Completed) &
                fb.Eq(x => x.IsDeleted, false))
            .SortBy(x => x.Id)
            .Limit(2)
            .ToListAsync(ct);
        if (jobs.Count > 1)
            throw Fail("COMPLETED_PUBLICATION_AMBIGUOUS");
        if (jobs.Count == 0)
            return null;

        await ValidatePublishedJobForLinkAsync(jobs[0], reportId, eventKey, ct);
        return jobs[0];
    }

    private async Task ValidatePublishedJobForLinkAsync(
        WorkReportStatisticRebuildJob job,
        string reportId,
        string eventKey,
        CancellationToken ct)
    {
        if (!ObjectId.TryParse(job.Id, out _) ||
            !string.Equals(job.RunKind, WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection, StringComparison.Ordinal) ||
            !string.Equals(job.CapabilityId, StatRunCapabilities.DirectFieldTableLabel, StringComparison.Ordinal) ||
            !string.Equals(job.RouteId, StatRunRouteRegistry.LifecycleDirectProjector, StringComparison.Ordinal) ||
            !string.Equals(job.SourceReportId, reportId, StringComparison.Ordinal) ||
            !string.Equals(job.SourceLifecycleEventKey, eventKey, StringComparison.Ordinal))
        {
            throw Fail("COMPLETED_PUBLICATION_INVALID");
        }

        // Link/replay stays compatible with immutable V1, P9-07 ledger V2 and
        // P9-08 reversal generations while still proving every live store digest.
        await ValidateCompletedPublicationAsync(job, requireCurrent: false, ct);
    }

    private async Task<IReadOnlyList<WorkReportDirectStoreDigest>> ComputePublishedJobDigestsAsync(
        WorkReportStatisticRebuildJob job,
        CancellationToken ct)
    {
        var fieldValues = await _ctx.WorkReportFieldStatValues
            .Find(x => x.DirectProjection != null &&
                       x.DirectProjection.GenerationId == job.GenerationId &&
                       !x.IsDeleted)
            .SortBy(x => x.Id)
            .ToListAsync(ct);
        var fieldAggregates = await _ctx.WorkReportFieldStatAggregates
            .Find(x => x.DirectProjection != null &&
                       x.DirectProjection.GenerationId == job.GenerationId &&
                       !x.IsDeleted)
            .SortBy(x => x.Id)
            .ToListAsync(ct);
        var tableValues = await _ctx.WorkReportTableStatValues
            .Find(x => x.DirectProjection != null &&
                       x.DirectProjection.GenerationId == job.GenerationId &&
                       !x.IsDeleted)
            .SortBy(x => x.Id)
            .ToListAsync(ct);
        var tableAggregates = await _ctx.WorkReportTableStatAggregates
            .Find(x => x.DirectProjection != null &&
                       x.DirectProjection.GenerationId == job.GenerationId &&
                       !x.IsDeleted)
            .SortBy(x => x.Id)
            .ToListAsync(ct);
        var labelValues = await _ctx.WorkReportLabelStatValues
            .Find(x => x.DirectProjection != null &&
                       x.DirectProjection.GenerationId == job.GenerationId &&
                       !x.IsDeleted)
            .SortBy(x => x.Id)
            .ToListAsync(ct);
        var labelAggregates = await _ctx.WorkReportLabelStatAggregates
            .Find(x => x.DirectProjection != null &&
                       x.DirectProjection.GenerationId == job.GenerationId &&
                       !x.IsDeleted)
            .SortBy(x => x.Id)
            .ToListAsync(ct);

        EnsurePublishedJobPins(fieldValues.Select(x => x.DirectProjection), job);
        EnsurePublishedJobPins(fieldAggregates.Select(x => x.DirectProjection), job);
        EnsurePublishedJobPins(tableValues.Select(x => x.DirectProjection), job);
        EnsurePublishedJobPins(tableAggregates.Select(x => x.DirectProjection), job);
        EnsurePublishedJobPins(labelValues.Select(x => x.DirectProjection), job);
        EnsurePublishedJobPins(labelAggregates.Select(x => x.DirectProjection), job);

        return new[]
        {
            Digest("work_report_field_stat_values", fieldValues),
            Digest("work_report_field_stat_aggregates", fieldAggregates),
            Digest("work_report_table_stat_values", tableValues),
            Digest("work_report_table_stat_aggregates", tableAggregates),
            Digest("work_report_label_stat_values", labelValues),
            Digest("work_report_label_stat_aggregates", labelAggregates)
        };
    }

    private static void EnsurePublishedJobPins(
        IEnumerable<WorkReportDirectProjectionPin?> pins,
        WorkReportStatisticRebuildJob job)
    {
        if (pins.Any(pin =>
                pin is null ||
                !ObjectId.TryParse(pin.SourceReportId, out _) ||
                pin.SourcePayloadRevision < 1 ||
                !StatRunCanonicalJson.IsCanonicalSha256(pin.SourcePayloadHash) ||
                pin.SourceLifecycleRevision < 1 ||
                !string.Equals(pin.RunId, job.Id, StringComparison.Ordinal) ||
                !string.Equals(pin.GenerationId, job.GenerationId, StringComparison.Ordinal) ||
                !string.Equals(pin.LifecycleEventKey, job.SourceLifecycleEventKey, StringComparison.Ordinal) ||
                pin.DirectSourceRevision != job.DirectSourceRevision ||
                !string.Equals(pin.DynamicFormFamilyId, job.DynamicFormFamilyId, StringComparison.Ordinal) ||
                !string.Equals(pin.DynamicFormTemplateId, job.DynamicFormTemplateId, StringComparison.Ordinal) ||
                pin.DynamicFormVersionNo != job.DynamicFormVersionNo ||
                !string.Equals(pin.DynamicFormSchemaHash, job.DynamicFormSchemaHash, StringComparison.Ordinal) ||
                !string.Equals(pin.ConfigId, job.ConfigId, StringComparison.Ordinal) ||
                !string.Equals(pin.ConfigVersionId, job.ConfigVersionId, StringComparison.Ordinal) ||
                pin.ConfigVersionNo != job.ConfigVersionNo ||
                pin.ConfigRevision != job.ConfigRevision ||
                !string.Equals(pin.ConfigHash, job.ConfigHash, StringComparison.Ordinal) ||
                !string.Equals(pin.CandidateChainId, job.CandidateChainId, StringComparison.Ordinal) ||
                !string.Equals(pin.CatalogVersion, job.CatalogVersion, StringComparison.Ordinal) ||
                !string.Equals(pin.CatalogRawSha256, job.CatalogRawSha256, StringComparison.Ordinal) ||
                !string.Equals(pin.CatalogSemanticSha256, job.CatalogSemanticSha256, StringComparison.Ordinal) ||
                !string.Equals(pin.SchemaRawSha256, job.SchemaRawSha256, StringComparison.Ordinal) ||
                !string.Equals(pin.SchemaSemanticSha256, job.SchemaSemanticSha256, StringComparison.Ordinal) ||
                !string.Equals(pin.StageLockSha256, job.StageLockSha256, StringComparison.Ordinal) ||
                !string.Equals(pin.SourceMembershipSignature, job.SourceMembershipSignature, StringComparison.Ordinal) ||
                !SameUtcMillisecond(pin.ComputedAtUtc, job.ComputedAtUtc!.Value)))
        {
            throw Fail("COMPLETED_PUBLICATION_PIN_MISMATCH");
        }
    }

    private async Task<WorkReportLifecycleProjectionOutboxEntry> LinkAsync(
        string reportId,
        string eventKey,
        string state,
        string reason,
        string? runId,
        string? generationId,
        string? generationHash,
        DateTime completedAtUtc,
        CancellationToken ct)
    {
        var observed = await LoadLifecycleEntryForLinkAsync(reportId, eventKey, ct);
        var existingState = observed.Entry.DirectProjectionState;
        if (string.Equals(existingState, StatRunDirectProjectionStates.Published, StringComparison.Ordinal))
        {
            if (string.Equals(state, StatRunDirectProjectionStates.ZeroWrite, StringComparison.Ordinal))
                return observed.Entry;
            EnsureExactTerminalLink(observed.Entry, state, runId, generationId, generationHash);
            return observed.Entry;
        }
        if (string.Equals(existingState, StatRunDirectProjectionStates.ZeroWrite, StringComparison.Ordinal) &&
            string.Equals(state, StatRunDirectProjectionStates.ZeroWrite, StringComparison.Ordinal))
        {
            return observed.Entry;
        }
        if (!string.IsNullOrWhiteSpace(existingState) &&
            !string.Equals(existingState, StatRunDirectProjectionStates.ZeroWrite, StringComparison.Ordinal))
        {
            throw Fail("LIFECYCLE_LINK_STATE_CONFLICT");
        }

        BsonValue runIdValue = runId is null
            ? BsonNull.Value
            : new BsonObjectId(ObjectId.Parse(runId));
        var update = Builders<WorkAssignmentReport>.Update
            .Set("lifecycleProjectionOutbox.$[entry].directProjectionState", state)
            .Set("lifecycleProjectionOutbox.$[entry].directProjectionReason", reason)
            .Set("lifecycleProjectionOutbox.$[entry].directProjectionRunId", runIdValue)
            .Set("lifecycleProjectionOutbox.$[entry].directProjectionGenerationId", generationId)
            .Set("lifecycleProjectionOutbox.$[entry].directProjectionGenerationHash", generationHash)
            .Set("lifecycleProjectionOutbox.$[entry].directProjectionCompletedAtUtc", completedAtUtc);
        var stateEligibility = string.Equals(state, StatRunDirectProjectionStates.Published, StringComparison.Ordinal)
            ? new BsonArray
            {
                new BsonDocument("entry.directProjectionState", new BsonDocument("$exists", false)),
                new BsonDocument("entry.directProjectionState", BsonNull.Value),
                new BsonDocument("entry.directProjectionState", StatRunDirectProjectionStates.ZeroWrite)
            }
            : new BsonArray
            {
                new BsonDocument("entry.directProjectionState", new BsonDocument("$exists", false)),
                new BsonDocument("entry.directProjectionState", BsonNull.Value)
            };
        var entryArrayFence = new BsonDocument("$and", new BsonArray
        {
            new BsonDocument("entry.entryKey", eventKey),
            new BsonDocument("entry.commandId", observed.Entry.CommandId),
            new BsonDocument("entry.lifecycleRevision", observed.Entry.LifecycleRevision),
            new BsonDocument("entry.operation", observed.Entry.Operation),
            new BsonDocument("entry.state", observed.Entry.State),
            new BsonDocument("entry.payloadRevision", observed.Entry.PayloadRevision),
            new BsonDocument(
                "entry.payloadHash",
                observed.Entry.PayloadHash is null
                    ? BsonNull.Value
                    : new BsonString(observed.Entry.PayloadHash)),
            new BsonDocument("$or", stateEligibility)
        });
        var options = new UpdateOptions
        {
            ArrayFilters =
            [
                new BsonDocumentArrayFilterDefinition<BsonDocument>(entryArrayFence)
            ]
        };
        var ef = Builders<WorkReportLifecycleProjectionOutboxEntry>.Filter;
        var eligibleState = ef.Exists(x => x.DirectProjectionState, false) |
                            ef.Eq(x => x.DirectProjectionState, null);
        if (string.Equals(state, StatRunDirectProjectionStates.Published, StringComparison.Ordinal))
            eligibleState |= ef.Eq(x => x.DirectProjectionState, StatRunDirectProjectionStates.ZeroWrite);
        var exactEntry = ef.Eq(x => x.EntryKey, eventKey) &
                         ef.Eq(x => x.CommandId, observed.Entry.CommandId) &
                         ef.Eq(x => x.LifecycleRevision, observed.Entry.LifecycleRevision) &
                         ef.Eq(x => x.Operation, observed.Entry.Operation) &
                         ef.Eq(x => x.State, observed.Entry.State) &
                         ef.Eq(x => x.PayloadRevision, observed.Entry.PayloadRevision) &
                         ef.Eq(x => x.PayloadHash, observed.Entry.PayloadHash) &
                         eligibleState;
        var rf = Builders<WorkAssignmentReport>.Filter;
        var result = await _ctx.WorkAssignmentReports.UpdateOneAsync(
            rf.Eq(x => x.Id, reportId) &
            rf.Eq(x => x.IsDeleted, false) &
            rf.ElemMatch(x => x.LifecycleProjectionOutbox, exactEntry),
            update,
            options,
            ct);
        var terminal = await LoadLifecycleEntryForLinkAsync(reportId, eventKey, ct);
        if (result.MatchedCount != 1 &&
            string.Equals(state, StatRunDirectProjectionStates.Published, StringComparison.Ordinal) &&
            !string.Equals(terminal.Entry.DirectProjectionState, StatRunDirectProjectionStates.Published, StringComparison.Ordinal))
        {
            throw Fail("LIFECYCLE_LINK_LOST");
        }
        if (string.Equals(state, StatRunDirectProjectionStates.ZeroWrite, StringComparison.Ordinal) &&
            string.Equals(terminal.Entry.DirectProjectionState, StatRunDirectProjectionStates.Published, StringComparison.Ordinal))
        {
            return terminal.Entry;
        }
        EnsureExactTerminalLink(terminal.Entry, state, runId, generationId, generationHash);
        return terminal.Entry;
    }

    private async Task<(WorkAssignmentReport Report, WorkReportLifecycleProjectionOutboxEntry Entry)>
        LoadLifecycleEntryForLinkAsync(string reportId, string eventKey, CancellationToken ct)
    {
        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw Fail("LIFECYCLE_LINK_REPORT_MISSING");
        var matches = (report.LifecycleProjectionOutbox ?? new List<WorkReportLifecycleProjectionOutboxEntry>())
            .Where(item => string.Equals(item.EntryKey, eventKey, StringComparison.Ordinal))
            .ToList();
        if (matches.Count != 1)
            throw Fail("LIFECYCLE_LINK_EVENT_AMBIGUOUS");
        var entry = matches[0];
        if (!string.Equals(
                entry.EntryKey,
                WorkReportLifecycleOutboxContract.ComputeEntryKey(
                    entry.CommandId,
                    entry.LifecycleRevision,
                    entry.Operation),
                StringComparison.Ordinal) ||
            !(IsInitialApprovalEntry(entry) ||
              IsReactivationEntry(entry) ||
              IsNonEffectiveLifecycleEntry(entry)) ||
            entry.State is not (
                WorkReportLifecycleProjectionOutboxStates.Pending or
                WorkReportLifecycleProjectionOutboxStates.Completed))
        {
            throw Fail("LIFECYCLE_LINK_EVENT_INVALID");
        }
        return (report, entry);
    }

    private static void EnsureExactTerminalLink(
        WorkReportLifecycleProjectionOutboxEntry entry,
        string state,
        string? runId,
        string? generationId,
        string? generationHash)
    {
        if (!string.Equals(entry.DirectProjectionState, state, StringComparison.Ordinal) ||
            !string.Equals(entry.DirectProjectionRunId, runId, StringComparison.Ordinal) ||
            !string.Equals(entry.DirectProjectionGenerationId, generationId, StringComparison.Ordinal) ||
            !string.Equals(entry.DirectProjectionGenerationHash, generationHash, StringComparison.Ordinal) ||
            !entry.DirectProjectionCompletedAtUtc.HasValue)
        {
            throw Fail("LIFECYCLE_LINK_TERMINAL_CONFLICT");
        }
    }

    private async Task ReleaseForRetryAsync(
        WorkReportStatisticRebuildJob job,
        string workerId,
        string claimToken,
        string diagnostic,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var retryCount = job.RetryCount + 1;
        var dead = retryCount >= MaxRetryCount;
        var nextStatus = dead
            ? WorkReportStatisticRebuildJobStatuses.DeadLetter
            : WorkReportStatisticRebuildJobStatuses.RetryWaiting;
        var nextRetryAtUtc = dead
            ? (DateTime?)null
            : now.AddMinutes(Math.Min(60, retryCount * 5));
        var nextRevision = job.StateRevision + 1;
        var nextHash = BuildStateHash(
            job.Id,
            nextStatus,
            nextRevision,
            null,
            null,
            job.GenerationId,
            null);
        try
        {
            await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
                x => x.Id == job.Id &&
                     x.Status == WorkReportStatisticRebuildJobStatuses.Running &&
                     x.LeaseOwnerId == workerId &&
                     x.ClaimToken == claimToken &&
                     x.StateRevision == job.StateRevision &&
                     x.StateHash == job.StateHash &&
                     !x.IsDeleted,
                Builders<WorkReportStatisticRebuildJob>.Update
                    .Set(x => x.Status, nextStatus)
                    .Set(x => x.IsActive, !dead)
                    .Set(x => x.NextRetryAtUtc, nextRetryAtUtc)
                    .Set(x => x.LeaseUntilUtc, null)
                    .Set(x => x.ClaimToken, null)
                    .Set(x => x.LeaseOwnerId, null)
                    .Set(x => x.DiagnosticCode, diagnostic)
                    .Set(x => x.FreshnessState, WorkReportStatisticRebuildJobFreshnessStates.Pending)
                    .Set(x => x.RetryCount, retryCount)
                    .Inc(x => x.FailedReportCount, 1)
                    .Set(x => x.CompletedAtUtc, dead ? now : null)
                    .Set(x => x.StateRevision, nextRevision)
                    .Set(x => x.StateHash, nextHash)
                    .Set(x => x.UpdatedAtUtc, now),
                cancellationToken: ct);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not release P9 Direct run fence. runId={RunId}",
                job.Id);
        }
    }

    private async Task<WorkReportStatisticRebuildJob?> TerminalizeStaleRunAsync(
        WorkReportStatisticRebuildJob job,
        string reason,
        string reportId,
        string eventKey,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var nextRevision = job.StateRevision + 1;
        var nextHash = BuildStateHash(
            job.Id,
            WorkReportStatisticRebuildJobStatuses.DeadLetter,
            nextRevision,
            null,
            null,
            job.GenerationId,
            null);
        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            fb.Eq(x => x.Id, job.Id) &
            fb.Eq(x => x.StateRevision, job.StateRevision) &
            fb.Eq(x => x.StateHash, job.StateHash) &
            fb.Ne(x => x.Status, WorkReportStatisticRebuildJobStatuses.Completed) &
            fb.Ne(x => x.Status, WorkReportStatisticRebuildJobStatuses.DeadLetter) &
            fb.Eq(x => x.IsDeleted, false),
            Builders<WorkReportStatisticRebuildJob>.Update
                .Set(x => x.Status, WorkReportStatisticRebuildJobStatuses.DeadLetter)
                .Set(x => x.IsActive, false)
                .Set(x => x.IsCurrentPublication, false)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.ClaimToken, null)
                .Set(x => x.LeaseOwnerId, null)
                .Set(x => x.NextRetryAtUtc, null)
                .Set(x => x.CompletedAtUtc, now)
                .Set(x => x.FreshnessState, WorkReportStatisticRebuildJobFreshnessStates.Stale)
                .Set(x => x.StaleReason, reason)
                .Set(x => x.DiagnosticCode, reason)
                .Set(x => x.StateRevision, nextRevision)
                .Set(x => x.StateHash, nextHash)
                .Set(x => x.UpdatedAtUtc, now),
            cancellationToken: ct);
        if (result.MatchedCount == 1)
            return null;

        var observed = await _ctx.WorkReportStatisticRebuildJobs
            .Find(x => x.Id == job.Id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw Fail("STALE_RUN_TERMINALIZATION_LOST");
        if (string.Equals(
                observed.Status,
                WorkReportStatisticRebuildJobStatuses.Completed,
                StringComparison.Ordinal))
        {
            await ValidatePublishedJobForLinkAsync(observed, reportId, eventKey, ct);
            return observed;
        }
        if (!string.Equals(
                observed.Status,
                WorkReportStatisticRebuildJobStatuses.DeadLetter,
                StringComparison.Ordinal))
        {
            throw Fail("STALE_RUN_TERMINALIZATION_LOST");
        }
        return null;
    }

    private static bool IsTerminalStaleFailure(Exception exception)
    {
        var reason = exception.Message;
        return reason is
            "P9_DIRECT_CANDIDATE_PIN_STALE" or
            "P9_DIRECT_CONFIG_PIN_STALE" or
            "P9_DIRECT_SOURCE_REVISION_STALE" or
            "P9_DIRECT_MEMBERSHIP_STALE" or
            "P9_DIRECT_STALE_SOURCE_REVISION_FENCE";
    }

    internal static bool ShouldTerminalizeClaimedProjectionFailure(
        bool foundationRefresh,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return foundationRefresh &&
               StatRunFoundationDiagnostics
                   .ClassifyProjectionFailure(exception)
                   .Disposition == StatRunFoundationFailureDisposition.Terminal;
    }

    private static bool IsPermanentBackfillFailure(Exception exception)
    {
        var reason = exception.Message;
        return reason.StartsWith("P9_DIRECT_", StringComparison.Ordinal) &&
               reason is not
                   "P9_DIRECT_RUN_CLAIM_BUSY" and not
                   "P9_DIRECT_STALE_PUBLICATION_FENCE" and not
                   "P9_DIRECT_LIFECYCLE_LINK_LOST";
    }

    private Task<StatRunDirectProjectionResult> CompleteZeroWriteAsync(
        string reportId,
        string lifecycleEventKey,
        string reason,
        bool linkLifecycleEntry,
        CancellationToken ct)
        => linkLifecycleEntry
            ? LinkZeroWriteAsync(reportId, lifecycleEventKey, reason, ct)
            : Task.FromResult(new StatRunDirectProjectionResult(
                StatRunDirectProjectionStates.ZeroWrite,
                reason,
                null,
                null,
                null,
                false));

    private static void ValidateFoundationRefreshPinShape(
        StatRunFoundationRefreshPin pin,
        string reportId)
    {
        if (!string.Equals(pin.CapabilityId, StatRunCapabilities.DirectFieldTableLabel, StringComparison.Ordinal) ||
            !string.Equals(pin.RunKind, WorkReportStatisticRebuildJobRunKinds.Foundation, StringComparison.Ordinal) ||
            !string.Equals(pin.SourceReportId, reportId, StringComparison.Ordinal) ||
            !ObjectId.TryParse(pin.WorkId, out _) ||
            string.IsNullOrWhiteSpace(pin.ScopeType) ||
            (pin.ScopeId is not null && !ObjectId.TryParse(pin.ScopeId, out _)) ||
            pin.SourceRevision < 1 ||
            !StatRunCanonicalJson.IsCanonicalSha256(pin.SourceHash) ||
            pin.LifecycleRevision < 1 ||
            !ObjectId.TryParse(pin.DynamicFormTemplateId, out _) ||
            !ObjectId.TryParse(pin.ConfigId, out _) ||
            !ObjectId.TryParse(pin.ConfigVersionId, out _) ||
            pin.ConfigVersionNo < 1 ||
            pin.ConfigRevision < 1 ||
            !StatRunCanonicalJson.IsCanonicalSha256(pin.ConfigHash) ||
            string.IsNullOrWhiteSpace(pin.CatalogVersion) ||
            !StatRunCanonicalJson.IsCanonicalSha256(pin.CatalogRawSha256) ||
            !StatRunCanonicalJson.IsCanonicalSha256(pin.CatalogSemanticSha256) ||
            !StatRunCanonicalJson.IsCanonicalSha256(pin.SchemaRawSha256) ||
            !StatRunCanonicalJson.IsCanonicalSha256(pin.SchemaSemanticSha256) ||
            !StatRunCanonicalJson.IsCanonicalSha256(pin.StageLockSha256) ||
            string.IsNullOrWhiteSpace(pin.CandidateChainId) ||
            string.IsNullOrWhiteSpace(pin.PeriodKey) ||
            string.IsNullOrWhiteSpace(pin.PeriodInstanceKey) ||
            string.IsNullOrWhiteSpace(pin.PeriodKind))
        {
            throw Fail("FOUNDATION_REFRESH_PIN_INVALID");
        }
    }

    private static void ValidateFoundationRefreshSourcePin(
        StatRunFoundationRefreshPin pin,
        DirectSource source)
    {
        if (!string.Equals(pin.SourceReportId, source.Report.Id, StringComparison.Ordinal) ||
            !string.Equals(pin.WorkId, source.Report.WorkId, StringComparison.Ordinal) ||
            pin.SourceRevision != source.Report.PayloadRevision ||
            !string.Equals(pin.SourceHash, source.Report.PayloadHash, StringComparison.Ordinal) ||
            pin.LifecycleRevision != source.Report.LifecycleRevision ||
            !string.Equals(pin.DynamicFormTemplateId, source.Report.DynamicFormTemplateId, StringComparison.Ordinal) ||
            !FoundationScopeMatches(
                pin.ScopeType,
                pin.ScopeId,
                source.Report.WorkId,
                source.Report.WorkAssignmentId,
                source.Assignment.RootAssignmentId) ||
            !string.Equals(pin.PeriodKey, source.Report.PeriodKey, StringComparison.Ordinal) ||
            !string.Equals(pin.PeriodInstanceKey, source.Report.PeriodInstanceKey, StringComparison.Ordinal) ||
            !string.Equals(pin.PeriodKind, source.Report.PeriodKind, StringComparison.Ordinal) ||
            !SameNullableUtcMillisecond(pin.PeriodStartUtc, NormalizeUtc(source.Report.PeriodStart)) ||
            !SameNullableUtcMillisecond(pin.PeriodEndUtc, NormalizeUtc(source.Report.PeriodEnd)))
        {
            throw Fail("FOUNDATION_REFRESH_SOURCE_PIN_STALE");
        }
    }

    internal static bool FoundationScopeMatches(
        string scopeType,
        string? scopeId,
        string workId,
        string workAssignmentId,
        string? rootAssignmentId)
        => scopeType switch
        {
            "WORK" => string.Equals(
                scopeId,
                workId,
                StringComparison.Ordinal),
            "ASSIGNMENT" => string.Equals(
                scopeId,
                workAssignmentId,
                StringComparison.Ordinal),
            "ROOT" => string.Equals(
                scopeId,
                rootAssignmentId,
                StringComparison.Ordinal),
            _ => false
        };
    internal static bool FoundationRefreshBindingContractMatches(
        StatRunFoundationRefreshPin pin,
        StatRunCandidateBinding foundationRequestBinding,
        StatRunCandidateBinding directProjectionBinding)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(foundationRequestBinding);
        ArgumentNullException.ThrowIfNull(directProjectionBinding);

        return string.Equals(
                   pin.CatalogVersion,
                   foundationRequestBinding.CatalogVersion,
                   StringComparison.Ordinal) &&
               string.Equals(
                   pin.CatalogRawSha256,
                   foundationRequestBinding.CatalogRawSha256,
                   StringComparison.Ordinal) &&
               string.Equals(
                   pin.CatalogSemanticSha256,
                   foundationRequestBinding.CatalogSemanticSha256,
                   StringComparison.Ordinal) &&
               string.Equals(
                   pin.SchemaRawSha256,
                   foundationRequestBinding.SchemaRawSha256,
                   StringComparison.Ordinal) &&
               string.Equals(
                   pin.SchemaSemanticSha256,
                   foundationRequestBinding.SchemaSemanticSha256,
                   StringComparison.Ordinal) &&
               string.Equals(
                   pin.StageLockSha256,
                   foundationRequestBinding.StageLockSha256,
                   StringComparison.Ordinal) &&
               string.Equals(
                   pin.CandidateChainId,
                   foundationRequestBinding.ChainId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   foundationRequestBinding.ChainId,
                   directProjectionBinding.ChainId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   foundationRequestBinding.DatabaseName,
                   directProjectionBinding.DatabaseName,
                   StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(directProjectionBinding.PromptId) &&
               !string.IsNullOrWhiteSpace(directProjectionBinding.CatalogVersion) &&
               StatRunCanonicalJson.IsCanonicalSha256(
                   directProjectionBinding.CatalogRawSha256) &&
               StatRunCanonicalJson.IsCanonicalSha256(
                   directProjectionBinding.CatalogSemanticSha256) &&
               StatRunCanonicalJson.IsCanonicalSha256(
                   directProjectionBinding.SchemaRawSha256) &&
               StatRunCanonicalJson.IsCanonicalSha256(
                   directProjectionBinding.SchemaSemanticSha256) &&
               StatRunCanonicalJson.IsCanonicalSha256(
                   directProjectionBinding.StageLockSha256);
    }

    private static void ValidateFoundationRefreshResolvedPin(
        StatRunFoundationRefreshPin pin,
        DirectSource source,
        DirectMember triggerMember,
        DynamicFormTemplate template)
    {
        if (!string.Equals(pin.ConfigId, template.StatisticConfigId, StringComparison.Ordinal) ||
            !string.Equals(pin.ConfigVersionId, template.StatisticConfigVersionId, StringComparison.Ordinal) ||
            pin.ConfigVersionNo != template.StatisticConfigVersionNo ||
            pin.ConfigRevision != template.StatisticConfigRevision ||
            !string.Equals(pin.ConfigHash, template.StatisticConfigHash, StringComparison.Ordinal) ||
            !string.Equals(pin.FlowTemplateId, source.Assignment.FlowTemplateId, StringComparison.Ordinal) ||
            pin.FlowFamilyRevision != triggerMember.Runtime.FlowFamilyRevision ||
            pin.FlowTemplateVersionNo != source.Assignment.FlowTemplateVersionNo ||
            !string.Equals(pin.FlowTemplateVersionId, triggerMember.Runtime.FlowTemplateVersionId, StringComparison.Ordinal) ||
            !string.Equals(pin.FlowPayloadHash, triggerMember.Runtime.FlowPayloadHash, StringComparison.Ordinal) ||
            !string.Equals(pin.FlowCatalogVersion, triggerMember.Runtime.FlowCatalogVersion, StringComparison.Ordinal) ||
            !string.Equals(pin.FlowCatalogSemanticHash, triggerMember.Runtime.FlowCatalogSemanticHash, StringComparison.Ordinal) ||
            !string.Equals(pin.FlowInstanceId, triggerMember.Runtime.FlowInstanceId, StringComparison.Ordinal) ||
            pin.FlowInstanceRevision != triggerMember.Runtime.FlowInstanceRevision ||
            !string.Equals(pin.FlowInstanceState, triggerMember.Runtime.FlowInstanceState, StringComparison.Ordinal) ||
            pin.FlowExecutionEpoch != source.Assignment.FlowExecutionEpoch ||
            !string.Equals(pin.FlowExecutionEpochId, triggerMember.Runtime.ExecutionEpochId, StringComparison.Ordinal) ||
            pin.FlowExecutionEpochRevision != triggerMember.Runtime.ExecutionEpochRevision ||
            !string.Equals(pin.FlowExecutionEpochState, triggerMember.Runtime.ExecutionEpochState, StringComparison.Ordinal) ||
            !string.Equals(pin.FlowBranchId, source.Assignment.FlowBranchId, StringComparison.Ordinal) ||
            !string.Equals(pin.FlowStepId, source.Assignment.FlowStepId, StringComparison.Ordinal) ||
            pin.FlowAttemptNo != source.Assignment.FlowAttemptNo ||
            !string.Equals(pin.FlowStepInstanceId, triggerMember.Runtime.StepInstanceId, StringComparison.Ordinal) ||
            pin.FlowStepInstanceRevision != triggerMember.Runtime.StepInstanceRevision ||
            !string.Equals(pin.FlowStepInstanceState, triggerMember.Runtime.StepInstanceState, StringComparison.Ordinal))
        {
            throw Fail("FOUNDATION_REFRESH_RESOLVED_PIN_STALE");
        }
    }

    internal static StatRunDirectProjectionCanonicalIdentity
        BuildFoundationRefreshCanonicalIdentity(
            string reportId,
            string lifecycleEventKey,
            string publicationScopeKey,
            string membershipSignature)
    {
        var sourceEventIdentity = BuildSourceEventIdentity(reportId, lifecycleEventKey);
        publicationScopeKey = RequireHash(publicationScopeKey, "PUBLICATION_SCOPE_KEY_INVALID");
        membershipSignature = RequireHash(membershipSignature, "MEMBERSHIP_SIGNATURE_INVALID");
        var identityKey = StatRunCanonicalJson.HashObject(new
        {
            version = FoundationRefreshIdentitySchema,
            triggerKind = "FOUNDATION_REFRESH",
            sourceEventIdentity,
            publicationScopeKey,
            membershipSignature
        });
        return new StatRunDirectProjectionCanonicalIdentity(
            FoundationRefreshIdentityVersion,
            identityKey,
            StableObjectId(FoundationRefreshRunNamespace, identityKey),
            $"p9-fdn-refresh-v2:{identityKey}",
            StatRunCanonicalJson.HashText(
                $"{FoundationRefreshReceiptNamespace}\n{identityKey}"));
    }

    internal static string BuildFoundationRefreshGenerationId(
        string identityKey,
        string chainId,
        string promptId,
        string reportId,
        string lifecycleEventKey,
        string publicationScopeKey,
        string membershipSignature)
    {
        identityKey = RequireHash(
            identityKey,
            "FOUNDATION_REFRESH_PIN_INVALID");
        if (string.IsNullOrWhiteSpace(chainId))
            throw Fail("FOUNDATION_REFRESH_PIN_INVALID");
        if (string.IsNullOrWhiteSpace(promptId))
            throw Fail("FOUNDATION_REFRESH_PIN_INVALID");

        return StatRunCanonicalJson.HashObject(new
        {
            version = FoundationRefreshGenerationSchema,
            triggerKind = "FOUNDATION_REFRESH",
            identityKey,
            ChainId = chainId,
            PromptId = promptId,
            sourceReportId = RequireObjectId(reportId, "SOURCE_REPORT_ID_INVALID"),
            lifecycleEventKey = RequireHash(
                lifecycleEventKey,
                "LIFECYCLE_EVENT_KEY_INVALID"),
            publicationScopeKey = RequireHash(
                publicationScopeKey,
                "PUBLICATION_SCOPE_KEY_INVALID"),
            membershipSignature = RequireHash(
                membershipSignature,
                "MEMBERSHIP_SIGNATURE_INVALID")
        });
    }
    private async Task<DirectProjectionIdentity> ResolveProjectionIdentityAsync(
        DirectSource source,
        string publicationScopeKey,
        string membershipSignature,
        string legacyGenerationId,
        bool foundationRefresh,
        CancellationToken ct)
    {
        var sourceEventIdentity = BuildSourceEventIdentity(
            source.Report.Id,
            source.Entry.EntryKey);
        var legacy = new DirectProjectionIdentity(
            null,
            null,
            StableObjectId("P9_LFC_RUN", sourceEventIdentity),
            $"p9-lfc:{source.Report.Id}:{source.Entry.EntryKey}",
            StatRunCanonicalJson.HashText(
                $"P9_LIFECYCLE_DIRECT_RECEIPT_V2\n{sourceEventIdentity}"),
            !foundationRefresh);
        if (!foundationRefresh)
            return legacy;

        var legacyJob = await _ctx.WorkReportStatisticRebuildJobs
            .Find(x => x.Id == legacy.RunId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        var terminalState = source.Entry.DirectProjectionState;
        if (string.IsNullOrWhiteSpace(terminalState))
        {
            if (legacyJob is null ||
                HasSameLogicalProjection(
                    legacyJob, publicationScopeKey, membershipSignature, legacyGenerationId))
            {
                return legacy with { LinkLifecycleEntry = false };
            }
        }
        else if (string.Equals(
                     terminalState,
                     StatRunDirectProjectionStates.Published,
                     StringComparison.Ordinal))
        {
            if (legacyJob is null ||
                !string.Equals(source.Entry.DirectProjectionRunId, legacy.RunId, StringComparison.Ordinal) ||
                !string.Equals(source.Entry.DirectProjectionGenerationId, legacyJob.GenerationId, StringComparison.Ordinal) ||
                !string.Equals(source.Entry.DirectProjectionGenerationHash, legacyJob.GenerationHash, StringComparison.Ordinal) ||
                legacyJob.DirectProjectionIdentityVersion is not null ||
                legacyJob.DirectProjectionIdentityKey is not null)
            {
                throw Fail("FOUNDATION_REFRESH_LIFECYCLE_LINK_INVALID");
            }
            await ValidatePublishedJobForLinkAsync(
                legacyJob,
                source.Report.Id,
                source.Entry.EntryKey,
                ct);
            if (HasSameLogicalProjection(
                    legacyJob, publicationScopeKey, membershipSignature, legacyGenerationId))
            {
                return legacy with { LinkLifecycleEntry = false };
            }
        }
        else if (!string.Equals(
                     terminalState,
                     StatRunDirectProjectionStates.ZeroWrite,
                     StringComparison.Ordinal))
        {
            throw Fail("FOUNDATION_REFRESH_LIFECYCLE_LINK_INVALID");
        }

        var canonical = BuildFoundationRefreshCanonicalIdentity(
            source.Report.Id,
            source.Entry.EntryKey,
            publicationScopeKey,
            membershipSignature);
        return new DirectProjectionIdentity(
            canonical.Version,
            canonical.Key,
            canonical.RunId,
            canonical.DedupeKey,
            canonical.ReceiptId,
            false);
    }

    private static bool HasSameLogicalProjection(
        WorkReportStatisticRebuildJob job,
        string publicationScopeKey,
        string membershipSignature,
        string generationId)
        => job.DirectProjectionIdentityVersion is null &&
           job.DirectProjectionIdentityKey is null &&
           string.Equals(job.PublicationScopeKey, publicationScopeKey, StringComparison.Ordinal) &&
           string.Equals(job.SourceMembershipSignature, membershipSignature, StringComparison.Ordinal) &&
           string.Equals(job.GenerationId, generationId, StringComparison.Ordinal);

    private static FilterDefinition<WorkReportStatisticRebuildJob> IdentityFence(
        FilterDefinitionBuilder<WorkReportStatisticRebuildJob> filter,
        DirectProjectionIdentity identity)
        => identity.IsFoundationRefresh
            ? filter.Eq(x => x.DirectProjectionIdentityVersion, identity.Version) &
              filter.Eq(x => x.DirectProjectionIdentityKey, identity.Key)
            : filter.Eq(x => x.DirectProjectionIdentityVersion, null) &
              filter.Eq(x => x.DirectProjectionIdentityKey, null);
    internal static string ComputeFoundationRefreshImmutableHeaderHash(
        WorkReportStatisticRebuildJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return StatRunCanonicalJson.HashObject(new
        {
            version = FoundationRefreshHeaderSchema,
            identityVersion = job.DirectProjectionIdentityVersion,
            identityKey = job.DirectProjectionIdentityKey,
            job.Id,
            job.DedupeKey,
            job.ReceiptId,
            job.CapabilityId,
            job.RouteId,
            job.RunKind,
            job.ActorUserId,
            job.TenantUnitId,
            job.ScopeType,
            job.ScopeId,
            job.SourceReportId,
            job.SourcePayloadRevision,
            job.SourcePayloadHash,
            job.SourceLifecycleRevision,
            job.SourceLifecycleEventKey,
            job.SourceMembershipSignature,
            job.DirectSourceRevision,
            job.PublicationScopeKey,
            job.SourceStatus,
            job.GenerationId,
            job.DynamicFormFamilyId,
            job.DynamicFormTemplateId,
            job.DynamicFormVersionNo,
            job.DynamicFormSchemaHash,
            job.DynamicFormTemplateCode,
            job.DynamicFormTemplateName,
            job.ConfigId,
            job.ConfigVersionId,
            job.ConfigVersionNo,
            job.ConfigRevision,
            job.ConfigHash,
            job.CandidateChainId,
            job.CandidatePromptId,
            job.CatalogVersion,
            job.CatalogRawSha256,
            job.CatalogSemanticSha256,
            job.SchemaRawSha256,
            job.SchemaSemanticSha256,
            job.StageLockSha256,
            job.ScopeKind,
            job.WorkId,
            job.WorkAssignmentId,
            job.FlowInstanceId,
            job.FlowInstanceRevision,
            job.FlowInstanceState,
            job.FlowEffectiveStatus,
            job.FlowTemplateId,
            job.FlowFamilyRevision,
            job.FlowTemplateVersionNo,
            job.FlowTemplateVersionId,
            job.FlowContributionOriginVersionId,
            job.FlowPayloadHash,
            job.FlowCatalogVersion,
            job.FlowCatalogSemanticHash,
            job.FlowExecutionEpoch,
            job.FlowExecutionEpochId,
            job.FlowExecutionEpochRevision,
            job.FlowExecutionEpochState,
            job.FlowBranchId,
            job.FlowStepId,
            job.FlowAttemptNo,
            job.FlowStepInstanceId,
            job.FlowStepInstanceRevision,
            job.FlowStepInstanceState,
            job.FlowContributionPolicy,
            job.FlowContributionPolicyHash,
            job.FlowContributionWarning,
            job.FlowContributionOperationVersion,
            job.PeriodKey,
            job.PeriodInstanceKey,
            job.PeriodKind,
            periodStartUtc = NormalizeUtc(job.PeriodStartUtc),
            periodEndUtc = NormalizeUtc(job.PeriodEndUtc),
            job.TotalReportCount,
            computedAtUtc = NormalizeUtc(job.ComputedAtUtc)
        });
    }
    private static string BuildImmutableHeaderHash(
        WorkReportDirectGenerationContext context,
        DirectSource source,
        DirectMember triggerMember,
        StatRunCandidateBinding binding,
        string publicationScopeKey,
        string receiptId,
        string actorUserId,
        string tenantUnitId,
        int memberCount,
        WorkReportStatisticReversalAudit? reversalAudit)
    {
        var baseHeaderHash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_LIFECYCLE_DIRECT_HEADER_V2",
            context.RunId,
            receiptId,
            sourceReportId = source.Report.Id,
            lifecycleEventKey = source.Entry.EntryKey,
            source.Entry.CommandId,
            source.Report.PayloadRevision,
            source.Report.PayloadHash,
            source.Report.LifecycleRevision,
            context.DirectSourceRevision,
            publicationScopeKey,
            context.SourceMembershipSignature,
            context.GenerationId,
            context.DynamicFormFamilyId,
            context.DynamicFormTemplateId,
            context.DynamicFormVersionNo,
            context.DynamicFormSchemaHash,
            context.ConfigId,
            context.ConfigVersionId,
            context.ConfigVersionNo,
            context.ConfigRevision,
            context.ConfigHash,
            context.CandidateChainId,
            context.CatalogVersion,
            context.CatalogRawSha256,
            context.CatalogSemanticSha256,
            context.SchemaRawSha256,
            context.SchemaSemanticSha256,
            context.StageLockSha256,
            binding.PromptId,
            actorUserId,
            tenantUnitId,
            source.Report.WorkId,
            source.Report.WorkAssignmentId,
            source.Report.PeriodKey,
            source.Report.PeriodInstanceKey,
            source.Report.PeriodKind,
            periodStartUtc = NormalizeUtc(source.Report.PeriodStart),
            periodEndUtc = NormalizeUtc(source.Report.PeriodEnd),
            memberCount,
            source.Assignment.FlowEffectiveStatus,
            source.Assignment.FlowTemplateId,
            source.Assignment.FlowTemplateVersionNo,
            source.Assignment.FlowExecutionEpoch,
            source.Assignment.FlowBranchId,
            source.Assignment.FlowStepId,
            source.Assignment.FlowAttemptNo,
            triggerMember.Runtime.FlowFamilyRevision,
            triggerMember.Runtime.FlowTemplateVersionId,
            triggerMember.Runtime.FlowContributionOriginVersionId,
            triggerMember.Runtime.FlowPayloadHash,
            triggerMember.Runtime.FlowCatalogVersion,
            triggerMember.Runtime.FlowCatalogSemanticHash,
            triggerMember.Runtime.FlowInstanceId,
            triggerMember.Runtime.FlowInstanceRevision,
            triggerMember.Runtime.FlowInstanceState,
            triggerMember.Runtime.ExecutionEpochId,
            triggerMember.Runtime.ExecutionEpochRevision,
            triggerMember.Runtime.ExecutionEpochState,
            triggerMember.Runtime.StepInstanceId,
            triggerMember.Runtime.StepInstanceRevision,
            triggerMember.Runtime.StepInstanceState,
            context.ComputedAtUtc
        });
        if (reversalAudit is null)
            return baseHeaderHash;
        return StatRunCanonicalJson.HashObject(new
        {
            version = "P9_LIFECYCLE_DIRECT_REVERSAL_HEADER_V1",
            baseHeaderHash,
            reversalAudit.AuditHash,
            reversalAudit.PriorRunId,
            reversalAudit.PriorGenerationId,
            reversalAudit.PriorGenerationHash,
            reversalAudit.PriorLedgerHash,
            reversalAudit.PriorReversalBaselineHash
        });
    }

    private static void ValidateRunHeader(
        WorkReportStatisticRebuildJob job,
        WorkReportDirectGenerationContext context,
        DirectSource source,
        DirectMember triggerMember,
        DynamicFormTemplate template,
        StatRunCandidateBinding binding,
        DirectProjectionIdentity identity,
        string publicationScopeKey,
        string actorUserId,
        string tenantUnitId,
        int memberCount,
        WorkReportStatisticReversalAudit? reversalAudit)
    {
        var receiptId = identity.ReceiptId;
        var immutableHeaderHash = identity.IsFoundationRefresh
            ? ComputeFoundationRefreshImmutableHeaderHash(job)
            : BuildImmutableHeaderHash(
                context, source, triggerMember, binding, publicationScopeKey,
                receiptId, actorUserId, tenantUnitId, memberCount, reversalAudit);
        if (!string.Equals(job.Id, context.RunId, StringComparison.Ordinal) ||
            !string.Equals(job.DedupeKey, identity.DedupeKey, StringComparison.Ordinal) ||
            !string.Equals(job.ReceiptId, receiptId, StringComparison.Ordinal) ||
            !string.Equals(
                job.CommandId,
                identity.IsFoundationRefresh ? null : source.Entry.CommandId,
                StringComparison.Ordinal) ||
            !string.Equals(job.DirectProjectionIdentityVersion, identity.Version, StringComparison.Ordinal) ||
            !string.Equals(job.DirectProjectionIdentityKey, identity.Key, StringComparison.Ordinal) ||
            !string.Equals(job.RequestHash, immutableHeaderHash, StringComparison.Ordinal) ||
            !string.Equals(job.ImmutableHeaderHash, immutableHeaderHash, StringComparison.Ordinal) ||
            !string.Equals(job.RunKind, WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection, StringComparison.Ordinal) ||
            !string.Equals(job.CapabilityId, StatRunCapabilities.DirectFieldTableLabel, StringComparison.Ordinal) ||
            !string.Equals(job.RouteId, StatRunRouteRegistry.LifecycleDirectProjector, StringComparison.Ordinal) ||
            !string.Equals(job.ActorUserId, actorUserId, StringComparison.Ordinal) ||
            !string.Equals(job.TenantUnitId, tenantUnitId, StringComparison.Ordinal) ||
            !string.Equals(job.ScopeType, "WORK_PERIOD_TEMPLATE", StringComparison.Ordinal) ||
            !string.Equals(job.ScopeId, source.Report.WorkId, StringComparison.Ordinal) ||
            !string.Equals(job.SourceReportId, source.Report.Id, StringComparison.Ordinal) ||
            job.SourcePayloadRevision != source.Report.PayloadRevision ||
            !string.Equals(job.SourcePayloadHash, source.Report.PayloadHash, StringComparison.Ordinal) ||
            job.SourceLifecycleRevision != source.Report.LifecycleRevision ||
            !string.Equals(job.SourceLifecycleEventKey, context.LifecycleEventKey, StringComparison.Ordinal) ||
            !string.Equals(job.SourceMembershipSignature, context.SourceMembershipSignature, StringComparison.Ordinal) ||
            job.DirectSourceRevision != context.DirectSourceRevision ||
            !string.Equals(job.PublicationScopeKey, publicationScopeKey, StringComparison.Ordinal) ||
            !string.Equals(
                job.SourceStatus,
                source.Report.Status.ToString().ToUpperInvariant(),
                StringComparison.Ordinal) ||
            !string.Equals(job.GenerationId, context.GenerationId, StringComparison.Ordinal) ||
            !string.Equals(job.DynamicFormFamilyId, context.DynamicFormFamilyId, StringComparison.Ordinal) ||
            !string.Equals(job.DynamicFormTemplateId, context.DynamicFormTemplateId, StringComparison.Ordinal) ||
            job.DynamicFormVersionNo != context.DynamicFormVersionNo ||
            !string.Equals(job.DynamicFormSchemaHash, context.DynamicFormSchemaHash, StringComparison.Ordinal) ||
            !string.Equals(job.DynamicFormTemplateCode, template.Code, StringComparison.Ordinal) ||
            !string.Equals(job.DynamicFormTemplateName, template.Name, StringComparison.Ordinal) ||
            !string.Equals(job.ConfigId, context.ConfigId, StringComparison.Ordinal) ||
            !string.Equals(job.ConfigVersionId, context.ConfigVersionId, StringComparison.Ordinal) ||
            job.ConfigVersionNo != context.ConfigVersionNo ||
            job.ConfigRevision != context.ConfigRevision ||
            !string.Equals(job.ConfigHash, context.ConfigHash, StringComparison.Ordinal) ||
            !string.Equals(job.CandidateChainId, context.CandidateChainId, StringComparison.Ordinal) ||
            !string.Equals(job.CandidatePromptId, binding.PromptId, StringComparison.Ordinal) ||
            !string.Equals(job.CatalogVersion, context.CatalogVersion, StringComparison.Ordinal) ||
            !string.Equals(job.CatalogRawSha256, context.CatalogRawSha256, StringComparison.Ordinal) ||
            !string.Equals(job.CatalogSemanticSha256, context.CatalogSemanticSha256, StringComparison.Ordinal) ||
            !string.Equals(job.SchemaRawSha256, context.SchemaRawSha256, StringComparison.Ordinal) ||
            !string.Equals(job.SchemaSemanticSha256, context.SchemaSemanticSha256, StringComparison.Ordinal) ||
            !string.Equals(job.StageLockSha256, context.StageLockSha256, StringComparison.Ordinal) ||
            !string.Equals(job.ScopeKind, WorkReportStatisticRebuildJobScopeKinds.Bounded, StringComparison.Ordinal) ||
            !string.Equals(job.WorkId, source.Report.WorkId, StringComparison.Ordinal) ||
            !string.Equals(job.WorkAssignmentId, source.Report.WorkAssignmentId, StringComparison.Ordinal) ||
            !string.Equals(job.FlowInstanceId, triggerMember.Runtime.FlowInstanceId, StringComparison.Ordinal) ||
            job.FlowInstanceRevision != triggerMember.Runtime.FlowInstanceRevision ||
            !string.Equals(job.FlowInstanceState, triggerMember.Runtime.FlowInstanceState, StringComparison.Ordinal) ||
            !string.Equals(job.FlowEffectiveStatus, source.Assignment.FlowEffectiveStatus, StringComparison.Ordinal) ||
            !string.Equals(job.FlowTemplateId, source.Assignment.FlowTemplateId, StringComparison.Ordinal) ||
            job.FlowFamilyRevision != triggerMember.Runtime.FlowFamilyRevision ||
            job.FlowTemplateVersionNo != source.Assignment.FlowTemplateVersionNo ||
            !string.Equals(job.FlowTemplateVersionId, triggerMember.Runtime.FlowTemplateVersionId, StringComparison.Ordinal) ||
            !string.Equals(
                job.FlowContributionOriginVersionId,
                triggerMember.Runtime.FlowContributionOriginVersionId,
                StringComparison.Ordinal) ||
            !string.Equals(job.FlowPayloadHash, triggerMember.Runtime.FlowPayloadHash, StringComparison.Ordinal) ||
            !string.Equals(job.FlowCatalogVersion, triggerMember.Runtime.FlowCatalogVersion, StringComparison.Ordinal) ||
            !string.Equals(job.FlowCatalogSemanticHash, triggerMember.Runtime.FlowCatalogSemanticHash, StringComparison.Ordinal) ||
            job.FlowExecutionEpoch != source.Assignment.FlowExecutionEpoch ||
            !string.Equals(job.FlowExecutionEpochId, triggerMember.Runtime.ExecutionEpochId, StringComparison.Ordinal) ||
            job.FlowExecutionEpochRevision != triggerMember.Runtime.ExecutionEpochRevision ||
            !string.Equals(job.FlowExecutionEpochState, triggerMember.Runtime.ExecutionEpochState, StringComparison.Ordinal) ||
            !string.Equals(job.FlowBranchId, source.Assignment.FlowBranchId, StringComparison.Ordinal) ||
            !string.Equals(job.FlowStepId, source.Assignment.FlowStepId, StringComparison.Ordinal) ||
            job.FlowAttemptNo != source.Assignment.FlowAttemptNo ||
            !string.Equals(job.FlowStepInstanceId, triggerMember.Runtime.StepInstanceId, StringComparison.Ordinal) ||
            job.FlowStepInstanceRevision != triggerMember.Runtime.StepInstanceRevision ||
            !string.Equals(job.FlowStepInstanceState, triggerMember.Runtime.StepInstanceState, StringComparison.Ordinal) ||
            !string.Equals(job.FlowContributionPolicy, triggerMember.Runtime.LockedContributionPolicy, StringComparison.Ordinal) ||
            !string.Equals(job.FlowContributionPolicyHash, triggerMember.Runtime.LockedContributionPolicyHash, StringComparison.Ordinal) ||
            !string.Equals(job.FlowContributionWarning, triggerMember.Runtime.LockedContributionWarning, StringComparison.Ordinal) ||
            !string.Equals(job.FlowContributionOperationVersion, ContributionOperationVersion, StringComparison.Ordinal) ||
            !SameReversalAudit(job.ReversalAudit, reversalAudit) ||
            !string.Equals(job.PeriodKey, source.Report.PeriodKey, StringComparison.Ordinal) ||
            !string.Equals(job.PeriodInstanceKey, source.Report.PeriodInstanceKey, StringComparison.Ordinal) ||
            !string.Equals(job.PeriodKind, source.Report.PeriodKind, StringComparison.Ordinal) ||
            !SameNullableUtcMillisecond(job.PeriodStartUtc, NormalizeUtc(source.Report.PeriodStart)) ||
            !SameNullableUtcMillisecond(job.PeriodEndUtc, NormalizeUtc(source.Report.PeriodEnd)) ||
            job.TotalReportCount != memberCount ||
            !job.ComputedAtUtc.HasValue ||
            !SameUtcMillisecond(job.ComputedAtUtc.Value, context.ComputedAtUtc))
        {
            throw Fail("RUN_HEADER_CONFLICT");
        }
    }

    private async Task ValidateCompletedReplayAsync(
        WorkReportStatisticRebuildJob job,
        WorkReportDirectGenerationContext context,
        DirectSource source,
        DirectMember triggerMember,
        DynamicFormTemplate template,
        StatRunCandidateBinding binding,
        DirectProjectionIdentity identity,
        string publicationScopeKey,
        string actorUserId,
        string tenantUnitId,
        IReadOnlyCollection<DirectMember> members,
        ReversalContext? reversal,
        CancellationToken ct)
    {
        var reversalAudit = BuildReversalAudit(
            context,
            source,
            job.ReceiptId ?? throw Fail("RUN_RECEIPT_MISSING"),
            reversal);
        ValidateRunHeader(
            job, context, source, triggerMember, template, binding,
            identity, publicationScopeKey, actorUserId, tenantUnitId, members.Count,
            reversalAudit);
        if (!StatRunCanonicalJson.IsCanonicalSha256(job.GenerationHash) ||
            !job.PublishedAtUtc.HasValue ||
            job.DirectPublicationRevision is not > 0 ||
            !string.Equals(job.Status, WorkReportStatisticRebuildJobStatuses.Completed, StringComparison.Ordinal) ||
            job.IsActive ||
            job.DirectStoreDigests.Count != 6)
        {
            throw Fail("COMPLETED_RUN_INVALID");
        }
        var expectedStores = new[]
        {
            "work_report_field_stat_aggregates",
            "work_report_field_stat_values",
            "work_report_label_stat_aggregates",
            "work_report_label_stat_values",
            "work_report_table_stat_aggregates",
            "work_report_table_stat_values"
        };
        var stored = job.DirectStoreDigests
            .OrderBy(item => item.Store, StringComparer.Ordinal)
            .ToArray();
        if (!stored.Select(item => item.Store).SequenceEqual(expectedStores, StringComparer.Ordinal) ||
            stored.Any(item => item.RowCount < 0 || !StatRunCanonicalJson.IsCanonicalSha256(item.Sha256)))
        {
            throw Fail("COMPLETED_DIGEST_SET_INVALID");
        }
        var observed = (await ComputeAndValidateDigestsAsync(context, members, ct))
            .OrderBy(item => item.Store, StringComparer.Ordinal)
            .ToArray();
        if (stored.Zip(observed).Any(pair =>
                !string.Equals(pair.First.Store, pair.Second.Store, StringComparison.Ordinal) ||
                pair.First.RowCount != pair.Second.RowCount ||
                !string.Equals(pair.First.Sha256, pair.Second.Sha256, StringComparison.Ordinal)))
        {
            throw Fail("COMPLETED_DIGEST_REPLAY_MISMATCH");
        }
        var contributionAudit = await BuildFlowContributionAuditAsync(
            context,
            members,
            job.ReceiptId ?? throw Fail("RUN_RECEIPT_MISSING"),
            ct);
        ValidateStoredFlowContributionAudit(job, contributionAudit);
        var orderedDigests = observed
            .Select(item => new { item.Store, item.RowCount, item.Sha256 })
            .ToArray();
        var generationHash = reversalAudit is null
            ? StatRunCanonicalJson.HashObject(new
            {
                version = "P9_DIRECT_GENERATION_HASH_V3",
                context.GenerationId,
                context.SourceMembershipSignature,
                contributionAudit.LedgerHash,
                contributionAudit.ReversalBaselineHash,
                stores = orderedDigests
            })
            : StatRunCanonicalJson.HashObject(new
            {
                version = "P9_DIRECT_REVERSAL_GENERATION_HASH_V2",
                context.GenerationId,
                context.SourceMembershipSignature,
                contributionAudit.LedgerHash,
                contributionAudit.ReversalBaselineHash,
                reversalAuditHash = reversalAudit.AuditHash,
                stores = orderedDigests
            });
        if ((template.NativeTablesVersion is not null) != (job.NativeStatisticPublication is not null))
            throw Fail("NATIVE_PUBLICATION_REQUIRED");
        if (job.NativeStatisticPublication is not null)
        {
            var artifact = await NativeStatisticPublicationContract.ReadAsync(_ctx.Db, job, ct);
            var inputs = await ReloadNativeProjectionInputsAsync(source, template, binding, context,
                actorUserId, reversal is null, ct);
            NativeStatisticGenerationStage.Revalidate(artifact, inputs.Template, inputs.AuthorizedSources, ct);
        }
        generationHash = NativeStatisticPublicationContract.BindHash(generationHash, job.NativeStatisticPublication);
        if (!string.Equals(job.GenerationHash, generationHash, StringComparison.Ordinal))
            throw Fail("COMPLETED_GENERATION_HASH_MISMATCH");
    }

    private static void EnsureContributionPolicyWellFormed(WorkAssignmentReport report)
    {
        var mode = report.CumulativeContributionMode?.Trim().ToUpperInvariant();
        if (mode is not ("INCLUDE" or "EXCLUDE"))
            throw Fail("CONTRIBUTION_MODE_INVALID");
        if (string.IsNullOrWhiteSpace(report.CumulativeContributionPolicyJson))
            return;
        try
        {
            using var document = JsonDocument.Parse(report.CumulativeContributionPolicyJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw Fail("CONTRIBUTION_POLICY_INVALID");
            if (document.RootElement.TryGetProperty("rules", out var rules) &&
                rules.ValueKind != JsonValueKind.Array)
            {
                throw Fail("CONTRIBUTION_POLICY_INVALID");
            }
        }
        catch (JsonException)
        {
            throw Fail("CONTRIBUTION_POLICY_INVALID");
        }
    }

    private static string BuildStateHash(
        string runId,
        string status,
        long revision,
        string? claimToken,
        string? workerId,
        string? generationId,
        string? generationHash)
        => StatRunCanonicalJson.HashObject(new
        {
            version = "P9_LIFECYCLE_DIRECT_STATE_V1",
            runId,
            status,
            revision,
            claimToken,
            workerId,
            generationId,
            generationHash
        });

    private static string StableObjectId(string kind, string identity)
        => StatRunCanonicalJson.HashText($"{kind}\n{identity}")[..24];

    private static string BuildSourceEventIdentity(string reportId, string eventKey)
        => $"{RequireObjectId(reportId, "SOURCE_REPORT_ID_INVALID")}\n{RequireHash(eventKey, "LIFECYCLE_EVENT_KEY_INVALID")}";

    private static bool SameUtcMillisecond(DateTime left, DateTime right)
    {
        if (left.Kind == DateTimeKind.Unspecified || right.Kind == DateTimeKind.Unspecified)
            return false;
        return left.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond ==
               right.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond;
    }

    private static bool SameNullableUtcMillisecond(DateTime? left, DateTime? right)
        => left.HasValue == right.HasValue &&
           (!left.HasValue || SameUtcMillisecond(left.Value, right!.Value));

    private static string ResolveActor(
        WorkAssignmentReport report,
        WorkReportLifecycleProjectionOutboxEntry entry)
    {
        foreach (var value in new[]
                 {
                     entry.ActorUserId,
                     report.UpdatedByUserId,
                     report.CreatedByUserId,
                     report.AssigneeUserId
                 })
        {
            if (ObjectId.TryParse(value, out _))
                return value!;
        }
        throw Fail("ACTOR_USER_ID_INVALID");
    }

    private static string RequireObjectId(string? value, string reason)
        => ObjectId.TryParse(value?.Trim(), out var parsed)
            ? parsed.ToString()
            : throw Fail(reason);

    private static string RequireHash(string? value, string reason)
        => StatRunCanonicalJson.IsCanonicalSha256(value?.Trim())
            ? value!.Trim()
            : throw Fail(reason);

    private static DateTime? NormalizeUtc(DateTime? value)
        => value.HasValue
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
            : null;

    private static DateTime ResolveComputedAtUtc(
        WorkReportLifecycleProjectionOutboxEntry entry)
    {
        if (entry.CreatedAtUtc == default)
            throw Fail("LIFECYCLE_TIME_PIN_INVALID");
        return entry.CreatedAtUtc.Kind switch
        {
            DateTimeKind.Utc => entry.CreatedAtUtc,
            DateTimeKind.Local => entry.CreatedAtUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(entry.CreatedAtUtc, DateTimeKind.Utc)
        };
    }

    private static string StableDiagnostic(Exception exception)
    {
        var text = exception.Message.Trim().ToUpperInvariant();
        if (text.Length > 120)
            text = text[..120];
        return string.IsNullOrWhiteSpace(text) ? "DIRECT_PROJECTION_FAILED" : text;
    }

    private static InvalidOperationException Fail(string reason)
        => new($"P9_DIRECT_{reason}");

    private sealed record DirectProjectionIdentity(
        string? Version,
        string? Key,
        string RunId,
        string DedupeKey,
        string ReceiptId,
        bool LinkLifecycleEntry)
    {
        public bool IsFoundationRefresh =>
            string.Equals(Version, FoundationRefreshIdentityVersion, StringComparison.Ordinal);
    }

    private sealed record DirectSource(
        WorkAssignmentReport Report,
        WorkReportLifecycleProjectionOutboxEntry Entry,
        WorkAssignment Assignment,
        WorkReportPeriod Period);

    private sealed record DirectMember(
        WorkAssignmentReport Report,
        WorkAssignment Assignment,
        WorkReportPeriod Period,
        WorkReportLifecycleProjectionOutboxEntry ApprovalEntry,
        DirectTenantPin Tenant,
        DynamicFlowMappingLifecycleBinding? Mapping,
        DirectRuntimePin Runtime);

    private sealed record DirectTenantPin(
        string ApprovalActorUserId,
        string ApprovalActorUnitId,
        string IssuedByUnitId,
        IReadOnlyList<string> TargetUnitIds,
        string AssigneeUserId,
        string AssigneeUnitId);

    private sealed record DirectRuntimePin(
        int? FlowFamilyRevision,
        string? FlowTemplateVersionId,
        string? FlowContributionOriginVersionId,
        string? FlowPayloadHash,
        string? FlowCatalogVersion,
        string? FlowCatalogSemanticHash,
        string? FlowInstanceId,
        long? FlowInstanceRevision,
        string? FlowInstanceState,
        string? ExecutionEpochId,
        int? ExecutionEpoch,
        long? ExecutionEpochRevision,
        string? ExecutionEpochState,
        string? StepInstanceId,
        long? StepInstanceRevision,
        string? StepInstanceState,
        string? LockedContributionPolicy,
        string? LockedContributionPolicyHash,
        string? LockedContributionWarning)
    {
        public static readonly DirectRuntimePin NonFlow =
            new(
                null, null, null, null, null, null,
                null, null, null, null, null,
                null, null, null, null, null,
                null, null, null);
    }

    private sealed record FlowContributionAudit(
        IReadOnlyList<WorkReportFlowContributionSourceAudit> Sources,
        IReadOnlyList<WorkReportNonFlowContributionSourceAudit> NonFlowSources,
        IReadOnlyList<WorkReportFlowContributionTargetAudit> Targets,
        string LedgerHash,
        string ReversalBaselineHash);

    private sealed record ReversalContext(
        WorkReportStatisticRebuildJob PriorPublication,
        DirectMember TriggerMember,
        bool IsReplay);

    private sealed record RunClaim(
        WorkReportStatisticRebuildJob? Job,
        string? WorkerId,
        string? ClaimToken,
        WorkReportStatisticRebuildJob? Completed,
        string? TerminalReason)
    {
        public static RunClaim FromCompleted(WorkReportStatisticRebuildJob completed)
            => new(null, null, null, completed, null);

        public static RunClaim FromTerminal(string reason)
            => new(null, null, null, null, reason);
    }
}
