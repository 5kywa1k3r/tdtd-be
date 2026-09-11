using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.Services.DynamicFlows;

public enum DynamicFlowMappingIntegrityMode
{
    RequireCurrent,
    AllowHistorical
}

/// <summary>
/// Immutable identity of the mapping result that owns a report payload. Lifecycle
/// projections copy this value; they never reconstruct it from current rules/source.
/// </summary>
public sealed record DynamicFlowMappingLifecycleBinding(
    string ReceiptId,
    string ProvenanceId,
    string ProvenanceHash,
    int ResultPayloadRevision,
    string ResultPayloadHash)
{
    public static DynamicFlowMappingLifecycleBinding? FromReport(
        WorkAssignmentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var hasAny =
            !string.IsNullOrWhiteSpace(report.DynamicFlowMappingReceiptId) ||
            !string.IsNullOrWhiteSpace(report.DynamicFlowMappingProvenanceId) ||
            !string.IsNullOrWhiteSpace(report.DynamicFlowMappingProvenanceHash) ||
            report.DynamicFlowMappingResultPayloadRevision.HasValue ||
            !string.IsNullOrWhiteSpace(report.DynamicFlowMappingResultPayloadHash);
        if (!hasAny)
            return null;

        if (string.IsNullOrWhiteSpace(report.DynamicFlowMappingReceiptId) ||
            string.IsNullOrWhiteSpace(report.DynamicFlowMappingProvenanceId) ||
            !DynamicFlowMappingLifecycleContract.IsLowerSha256(
                report.DynamicFlowMappingProvenanceHash) ||
            report.DynamicFlowMappingResultPayloadRevision is not > 0 ||
            !DynamicFlowMappingLifecycleContract.IsLowerSha256(
                report.DynamicFlowMappingResultPayloadHash))
        {
            throw new InvalidOperationException(
                DynamicFlowMappingLifecycleContract.HeaderReferenceIncompleteReason);
        }

        return new DynamicFlowMappingLifecycleBinding(
            report.DynamicFlowMappingReceiptId.Trim(),
            report.DynamicFlowMappingProvenanceId.Trim(),
            report.DynamicFlowMappingProvenanceHash!,
            report.DynamicFlowMappingResultPayloadRevision.Value,
            report.DynamicFlowMappingResultPayloadHash!);
    }
}

public sealed record DynamicFlowMappingSuccessorPlan(
    bool IsRerun,
    string EventType,
    string OutboxOperation,
    string? PredecessorReceiptId,
    string? PredecessorProvenanceId,
    string SuccessorProvenanceId,
    string? InvalidationReason);

public sealed record DynamicFlowMappingInvalidationPlan(
    string EventId,
    string EventKey,
    string OutboxId,
    string OutboxDedupeKey,
    string Reason,
    string InvalidatedByEventId,
    string ProvenanceId,
    string ReceiptId,
    string TargetReportId,
    DateTime InvalidatedAtUtc);

public static class DynamicFlowMappingLifecycleContract
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

    public const string HeaderReferenceIncompleteReason =
        "DYNAMIC_FLOW_MAPPING_HEADER_REFERENCE_INCOMPLETE";
    public const string SourcePayloadDriftReason =
        "DYNAMIC_FLOW_MAPPING_SOURCE_PAYLOAD_DRIFT";
    public const string SourceLifecycleDriftReason =
        "DYNAMIC_FLOW_MAPPING_SOURCE_LIFECYCLE_DRIFT";
    public const string EpochInvalidatedReason =
        "DYNAMIC_FLOW_EXECUTION_EPOCH_INVALIDATED";
    public const string SupersededByRerunReason =
        "DYNAMIC_FLOW_MAPPING_SUPERSEDED_BY_RERUN";
    public const string RebuildIntentOperation =
        "REBUILD_DYNAMIC_FLOW_MAPPING_PROVENANCE";
    public const string ApplyRerunOperation =
        "RECONCILE_DYNAMIC_FLOW_MAPPING_RERUN";
    public const string ApplyInitialOperation =
        "RECONCILE_DYNAMIC_FLOW_MAPPING_APPLY";
    public const string LifecycleBlockedUntilPhase = "P7-09";
    public const string LifecycleBlockedReason =
        "DYNAMIC_FLOW_MAPPING_LIFECYCLE_BLOCKED_UNTIL_P7_09";
    public const string RerunBlockedReason =
        "DYNAMIC_FLOW_MAPPING_RERUN_BLOCKED_UNTIL_P7_09";
    public const string InvalidationBlockedReason =
        "DYNAMIC_FLOW_MAPPING_INVALIDATION_BLOCKED_UNTIL_P7_09";

    public static void EnsureP7LifecyclePhase(
        bool canLifecycleMapping,
        string operation,
        string reason,
        string? reportId = null,
        string? flowInstanceId = null)
    {
        if (canLifecycleMapping)
            return;

        throw AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE,
            new
            {
                field = "mappingLifecycle",
                reason = Required(reason, nameof(reason)),
                operation = Required(operation, nameof(operation)),
                reportId,
                flowInstanceId,
                executionEligibility = "BLOCKED_UNTIL_TARGET_PHASE",
                blockedUntilPhase = LifecycleBlockedUntilPhase,
                canExecute = false
            });
    }

    public static DynamicFlowMappingSuccessorPlan BuildSuccessorPlan(
        DynamicFlowMappingLifecycleBinding? predecessor,
        string successorProvenanceId)
    {
        successorProvenanceId = Required(
            successorProvenanceId,
            nameof(successorProvenanceId));
        return predecessor is null
            ? new DynamicFlowMappingSuccessorPlan(
                IsRerun: false,
                DynamicFlowMappingEventTypes.ApplyCommitted,
                ApplyInitialOperation,
                PredecessorReceiptId: null,
                PredecessorProvenanceId: null,
                successorProvenanceId,
                InvalidationReason: null)
            : new DynamicFlowMappingSuccessorPlan(
                IsRerun: true,
                DynamicFlowMappingEventTypes.RerunCommitted,
                ApplyRerunOperation,
                predecessor.ReceiptId,
                predecessor.ProvenanceId,
                successorProvenanceId,
                SupersededByRerunReason);
    }

    public static async Task<DynamicFlowMappingLifecycleBinding?>
        ValidateAsync(
            MongoDbContext ctx,
            WorkAssignmentReport report,
            DynamicFlowMappingIntegrityMode mode,
            CancellationToken ct,
            IClientSessionHandle? session = null,
            bool canLifecycleMapping = true,
            string? lifecycleOperation = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(report);

        DynamicFlowMappingLifecycleBinding? binding;
        try
        {
            binding = DynamicFlowMappingLifecycleBinding.FromReport(report);
        }
        catch (InvalidOperationException error) when (
            string.Equals(
                error.Message,
                HeaderReferenceIncompleteReason,
                StringComparison.Ordinal))
        {
            throw ProvenanceTampered(report, HeaderReferenceIncompleteReason);
        }
        if (binding is null)
            return null;
        if (!canLifecycleMapping)
        {
            EnsureP7LifecyclePhase(
                canLifecycleMapping,
                lifecycleOperation ?? "MAPPING_LIFECYCLE",
                LifecycleBlockedReason,
                reportId: report.Id);
        }

        DynamicFlowMappingApplyReceipt? receipt;
        DynamicFlowMappingProvenanceRecord? provenance;
        DynamicFlowMappingEvent? mappingEvent;
        DynamicFlowMappingOutboxItem? outbox;
        WorkReportPayload? payload;
        if (session is null)
        {
            receipt = await ctx.DynamicFlowMappingApplyReceipts
                .Find(item =>
                    item.Id == binding.ReceiptId &&
                    item.TargetReportId == report.Id)
                .FirstOrDefaultAsync(ct);
            provenance = await ctx.DynamicFlowMappingProvenanceRecords
                .Find(item =>
                    item.Id == binding.ProvenanceId &&
                    item.ReceiptId == binding.ReceiptId &&
                    item.TargetReportId == report.Id)
                .FirstOrDefaultAsync(ct);
            mappingEvent = receipt is null
                ? null
                : await ctx.DynamicFlowMappingEvents
                    .Find(item =>
                        item.Id == receipt.EventId &&
                        item.ReceiptId == receipt.Id)
                    .FirstOrDefaultAsync(ct);
            outbox = receipt is null
                ? null
                : await ctx.DynamicFlowMappingOutbox
                    .Find(item =>
                        item.Id == receipt.OutboxIntentId &&
                        item.ReceiptId == receipt.Id)
                    .FirstOrDefaultAsync(ct);
            payload = await ctx.WorkReportPayloads
                .Find(item =>
                    item.ReportId == report.Id &&
                    !item.IsDeleted)
                .FirstOrDefaultAsync(ct);
        }
        else
        {
            receipt = await ctx.DynamicFlowMappingApplyReceipts
                .Find(
                    session,
                    item =>
                        item.Id == binding.ReceiptId &&
                        item.TargetReportId == report.Id)
                .FirstOrDefaultAsync(ct);
            provenance = await ctx.DynamicFlowMappingProvenanceRecords
                .Find(
                    session,
                    item =>
                        item.Id == binding.ProvenanceId &&
                        item.ReceiptId == binding.ReceiptId &&
                        item.TargetReportId == report.Id)
                .FirstOrDefaultAsync(ct);
            mappingEvent = receipt is null
                ? null
                : await ctx.DynamicFlowMappingEvents
                    .Find(
                        session,
                        item =>
                            item.Id == receipt.EventId &&
                            item.ReceiptId == receipt.Id)
                    .FirstOrDefaultAsync(ct);
            outbox = receipt is null
                ? null
                : await ctx.DynamicFlowMappingOutbox
                    .Find(
                        session,
                        item =>
                            item.Id == receipt.OutboxIntentId &&
                            item.ReceiptId == receipt.Id)
                    .FirstOrDefaultAsync(ct);
            payload = await ctx.WorkReportPayloads
                .Find(
                    session,
                    item =>
                        item.ReportId == report.Id &&
                        !item.IsDeleted)
                .FirstOrDefaultAsync(ct);
        }

        if (receipt is null ||
            provenance is null ||
            mappingEvent is null ||
            outbox is null ||
            payload is null)
        {
            throw ProvenanceTampered(
                report,
                "DYNAMIC_FLOW_MAPPING_DURABLE_WRITE_SET_MISSING");
        }

        var writeSetHash = ComputeCanonicalHash(
            new
            {
                receiptId = receipt.Id,
                provenanceId = provenance.Id,
                eventId = mappingEvent.Id,
                eventKey = mappingEvent.EventKey,
                eventPayloadHash = mappingEvent.PayloadHash,
                outboxId = outbox.Id,
                outboxDedupeKey = outbox.DedupeKey,
                intentHash = outbox.IntentHash,
                targetReportId = report.Id,
                resultPayloadRevision = receipt.ResultPayloadRevision,
                resultPayloadHash = receipt.ResultPayloadHash,
                resultLifecycleRevision = receipt.ResultLifecycleRevision,
                provenanceHash = provenance.ProvenanceHash
            });
        var durableHashesValid =
            string.Equals(
                receipt.ResultSnapshotHash,
                ComputeDocumentHash(receipt.ResultSnapshot),
                StringComparison.Ordinal) &&
            string.Equals(
                provenance.ResultSnapshotHash,
                ComputeDocumentHash(provenance.ResultSnapshot),
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultSnapshotHash,
                provenance.ResultSnapshotHash,
                StringComparison.Ordinal) &&
            string.Equals(
                provenance.ProvenanceHash,
                ComputeDocumentHash(provenance.ProvenanceSnapshot),
                StringComparison.Ordinal) &&
            string.Equals(
                mappingEvent.PayloadHash,
                ComputeDocumentHash(mappingEvent.Payload),
                StringComparison.Ordinal) &&
            string.Equals(
                outbox.IntentHash,
                ComputeDocumentHash(outbox.Intent.ToBsonDocument()),
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.WriteSetHash,
                writeSetHash,
                StringComparison.Ordinal);
        var immutablePinsValid =
            string.Equals(
                ComputeCanonicalHash(receipt.RuntimePin),
                ComputeCanonicalHash(provenance.RuntimePin),
                StringComparison.Ordinal) &&
            string.Equals(
                ComputeCanonicalHash(receipt.SourcePins),
                ComputeCanonicalHash(provenance.SourcePins),
                StringComparison.Ordinal) &&
            provenance.SourcePins.All(pin =>
                string.Equals(
                    pin.SourceFactHash,
                    ComputeSourceFactHash(pin),
                    StringComparison.Ordinal));
        var referencesValid =
            string.Equals(
                receipt.ProvenanceId,
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ProvenanceHash,
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            string.Equals(
                binding.ProvenanceHash,
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            string.Equals(
                mappingEvent.ProvenanceId,
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                mappingEvent.ProvenanceHash,
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            string.Equals(
                outbox.ProvenanceId,
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                outbox.Intent.ProvenanceHash,
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.SourceSignature,
                provenance.SourceSignature,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultSemanticHash,
                provenance.ResultSemanticHash,
                StringComparison.Ordinal);
        var resultValid =
            receipt.ResultPayloadRevision == provenance.TargetPayloadRevision &&
            receipt.ResultPayloadRevision == binding.ResultPayloadRevision &&
            string.Equals(
                receipt.ResultPayloadHash,
                provenance.TargetPayloadHash,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultPayloadHash,
                binding.ResultPayloadHash,
                StringComparison.Ordinal) &&
            receipt.ResultLifecycleRevision ==
            provenance.TargetLifecycleRevision &&
            report.PayloadRevision >= receipt.ResultPayloadRevision &&
            (report.PayloadRevision != receipt.ResultPayloadRevision ||
             string.Equals(
                 report.PayloadHash,
                 receipt.ResultPayloadHash,
                 StringComparison.Ordinal));
        var currentPayloadValid =
            payload.PayloadRevision == report.PayloadRevision &&
            string.Equals(
                payload.PayloadHash,
                report.PayloadHash,
                StringComparison.Ordinal) &&
            string.Equals(
                payload.Status,
                WorkReportPayloadStatus.Ready,
                StringComparison.Ordinal) &&
            IsMappingSummary(payload.SummarySourceJson);
        if (!durableHashesValid ||
            !immutablePinsValid ||
            !referencesValid ||
            !resultValid ||
            !currentPayloadValid)
        {
            throw ProvenanceTampered(
                report,
                "DYNAMIC_FLOW_MAPPING_PROVENANCE_INTEGRITY_FAILED");
        }

        if (!IsStateAllowed(provenance.State, mode))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_MAPPING_LIFECYCLE_CONFLICT,
                new
                {
                    reportId = report.Id,
                    receiptId = binding.ReceiptId,
                    provenanceId = binding.ProvenanceId,
                    provenance.State,
                    integrityMode = mode.ToString(),
                    reason =
                        "DYNAMIC_FLOW_MAPPING_PROVENANCE_NOT_CURRENT"
                });
        }

        return binding;
    }

    public static DynamicFlowMappingInvalidationPlan BuildInvalidationPlan(
        DynamicFlowMappingProvenanceRecord provenance,
        string invalidatedByEventId,
        string reason,
        DateTime invalidatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        invalidatedByEventId = Required(
            invalidatedByEventId,
            nameof(invalidatedByEventId));
        reason = Required(reason, nameof(reason));
        var canonical = new
        {
            schemaVersion = "P7-MAP-INVALIDATION-1",
            provenanceId = Required(provenance.Id, nameof(provenance.Id)),
            receiptId = Required(provenance.ReceiptId, nameof(provenance.ReceiptId)),
            targetReportId = Required(
                provenance.TargetReportId,
                nameof(provenance.TargetReportId)),
            invalidatedByEventId,
            reason
        };
        var eventKey = ComputeCanonicalHash(canonical);
        return new DynamicFlowMappingInvalidationPlan(
            EventId: ComputeStableObjectId($"event\n{eventKey}"),
            EventKey: eventKey,
            OutboxId: ComputeStableObjectId($"outbox\n{eventKey}"),
            OutboxDedupeKey: ComputeCanonicalHash(
                new
                {
                    operation = RebuildIntentOperation,
                    eventKey
                }),
            Reason: reason,
            InvalidatedByEventId: invalidatedByEventId,
            ProvenanceId: provenance.Id,
            ReceiptId: provenance.ReceiptId,
            TargetReportId: provenance.TargetReportId,
            InvalidatedAtUtc: EnsureUtc(invalidatedAtUtc));
    }

    public static bool HasSourceDrift(
        DynamicFlowMappingSourcePin pin,
        WorkAssignmentReport source)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(source);
        return pin.SourcePayloadRevision != source.PayloadRevision ||
               !string.Equals(
                   pin.SourcePayloadHash,
                   source.PayloadHash,
                   StringComparison.Ordinal) ||
               pin.SourceLifecycleRevision != source.LifecycleRevision ||
               !string.Equals(
                   pin.SourceLifecycleStatus,
                   source.Status.ToString().ToUpperInvariant(),
                   StringComparison.Ordinal);
    }

    public static async Task EnsureSourceMutationPhaseAsync(
        MongoDbContext ctx,
        string sourceReportId,
        bool canLifecycleMapping,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        sourceReportId = Required(sourceReportId, nameof(sourceReportId));
        if (canLifecycleMapping)
            return;

        var hasCurrentDependent =
            await ctx.DynamicFlowMappingProvenanceRecords
                .Find(item =>
                    item.State ==
                    DynamicFlowMappingProvenanceStates.Current &&
                    item.SourcePins.Any(pin =>
                        pin.SourceReportId == sourceReportId))
                .AnyAsync(ct);
        if (!hasCurrentDependent)
            return;

        EnsureP7LifecyclePhase(
            canLifecycleMapping,
            "SOURCE_INVALIDATION",
            InvalidationBlockedReason,
            reportId: sourceReportId);
    }

    public static async Task<IReadOnlyList<string>>
        InvalidateSourceDependentsAsync(
            MongoDbContext ctx,
            IDynamicFlowDefinitionTransactionRunner transactions,
            WorkAssignmentReport source,
            string sourceEventId,
            string actorUserId,
            DateTime invalidatedAtUtc,
            bool canLifecycleMapping,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(source);
        sourceEventId = Required(sourceEventId, nameof(sourceEventId));
        actorUserId = Required(actorUserId, nameof(actorUserId));
        invalidatedAtUtc = EnsureUtc(invalidatedAtUtc);

        if (!canLifecycleMapping)
        {
            var currentDependents =
                await ctx.DynamicFlowMappingProvenanceRecords
                    .Find(item =>
                        item.State ==
                        DynamicFlowMappingProvenanceStates.Current &&
                        item.SourcePins.Any(pin =>
                            pin.SourceReportId == source.Id))
                    .ToListAsync(ct);
            var hasDriftedDependent = currentDependents.Any(
                provenance => provenance.SourcePins.Any(
                    pin =>
                        string.Equals(
                            pin.SourceReportId,
                            source.Id,
                            StringComparison.Ordinal) &&
                        HasSourceDrift(pin, source)));
            if (hasDriftedDependent)
            {
                EnsureP7LifecyclePhase(
                    canLifecycleMapping,
                    "SOURCE_INVALIDATION",
                    InvalidationBlockedReason,
                    reportId: source.Id);
            }

            return Array.Empty<string>();
        }

        return await transactions.ExecuteAsync<IReadOnlyList<string>>(
            async (session, transactionCt) =>
            {
                var current = await ctx.DynamicFlowMappingProvenanceRecords
                    .Find(
                        session,
                        item =>
                            item.State ==
                            DynamicFlowMappingProvenanceStates.Current &&
                            item.SourcePins.Any(pin =>
                                pin.SourceReportId == source.Id))
                    .ToListAsync(transactionCt);
                var candidates = current
                    .Where(provenance =>
                    {
                        var sourcePin = provenance.SourcePins.FirstOrDefault(
                            pin => string.Equals(
                                pin.SourceReportId,
                                source.Id,
                                StringComparison.Ordinal));
                        return sourcePin is not null &&
                               HasSourceDrift(sourcePin, source);
                    })
                    .OrderBy(item => item.Id, StringComparer.Ordinal)
                    .ToList();
                var targetReportIds = candidates
                    .Select(item => item.TargetReportId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var targetReports = targetReportIds.Count == 0
                    ? new List<WorkAssignmentReport>()
                    : await ctx.WorkAssignmentReports
                        .Find(
                            session,
                            report => targetReportIds.Contains(report.Id) &&
                                      !report.IsDeleted)
                        .ToListAsync(transactionCt);
                var targetReportById = targetReports
                    .GroupBy(report => report.Id, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Single(),
                        StringComparer.Ordinal);
                var targetWorkIds = targetReports
                    .Select(report => report.WorkId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var targetWorks = targetWorkIds.Count == 0
                    ? new List<Work>()
                    : await ctx.Works
                        .Find(
                            session,
                            work => targetWorkIds.Contains(work.Id) &&
                                    !work.IsDeleted)
                        .ToListAsync(transactionCt);
                var targetWorkById = targetWorks
                    .GroupBy(work => work.Id, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Single(),
                        StringComparer.Ordinal);
                var targetWorkIdByProvenanceId =
                    new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var provenance in candidates)
                {
                    if (string.IsNullOrWhiteSpace(provenance.TargetReportId) ||
                        !targetReportById.TryGetValue(
                            provenance.TargetReportId,
                            out var targetReport) ||
                        string.IsNullOrWhiteSpace(targetReport.WorkId) ||
                        !string.Equals(
                            targetReport.WorkAssignmentId,
                            provenance.TargetAssignmentId,
                            StringComparison.Ordinal) ||
                        !targetWorkById.ContainsKey(targetReport.WorkId))
                    {
                        throw new InvalidOperationException(
                            "P9_MAPPING_INVALIDATION_TARGET_OWNER_UNRESOLVED");
                    }

                    targetWorkIdByProvenanceId[provenance.Id] =
                        targetReport.WorkId;
                }

                var invalidatedIds = new List<string>();
                var affectedWorkIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var provenance in candidates)
                {
                    var pin = provenance.SourcePins.FirstOrDefault(item =>
                        string.Equals(
                            item.SourceReportId,
                            source.Id,
                            StringComparison.Ordinal));
                    if (pin is null || !HasSourceDrift(pin, source))
                        continue;

                    var reason = ResolveSourceDriftReason(pin, source);
                    var plan = BuildInvalidationPlan(
                        provenance,
                        sourceEventId,
                        reason,
                        invalidatedAtUtc);
                    var updated =
                        await ctx.DynamicFlowMappingProvenanceRecords
                            .UpdateOneAsync(
                                session,
                                item =>
                                    item.Id == provenance.Id &&
                                    item.State ==
                                    DynamicFlowMappingProvenanceStates.Current &&
                                    item.SupersededByProvenanceId == null,
                                Builders<DynamicFlowMappingProvenanceRecord>
                                    .Update
                                    .Set(
                                        item => item.State,
                                        DynamicFlowMappingProvenanceStates
                                            .Invalidated)
                                    .Set(
                                        item => item.InvalidatedByEventId,
                                        plan.InvalidatedByEventId)
                                    .Set(
                                        item => item.InvalidationReason,
                                        plan.Reason)
                                    .Set(
                                        item => item.InvalidatedAtUtc,
                                        plan.InvalidatedAtUtc),
                                cancellationToken: transactionCt);
                    if (updated.ModifiedCount != 1)
                        continue;

                    affectedWorkIds.Add(
                        targetWorkIdByProvenanceId[provenance.Id]);

                    var projectionSnapshot = new BsonDocument
                    {
                        { "schemaVersion", "P7-MAP-INVALIDATION-PROJECTION-1" },
                        { "targetReportId", provenance.TargetReportId },
                        { "sourceReportId", source.Id },
                        { "invalidatedProvenanceId", provenance.Id },
                        { "invalidatedByEventId", plan.InvalidatedByEventId },
                        { "reason", plan.Reason },
                        { "rebuildOnly", true },
                        { "p8ExecutionEnabled", false },
                        { "p9ExecutionEnabled", false }
                    };
                    var projectionSnapshotHash =
                        ComputeDocumentHash(projectionSnapshot);
                    var auditSnapshot = new BsonDocument
                    {
                        { "schemaVersion", "P7-MAP-INVALIDATION-AUDIT-1" },
                        { "receiptId", provenance.ReceiptId },
                        { "provenanceId", provenance.Id },
                        { "provenanceHash", provenance.ProvenanceHash },
                        { "sourceReportId", source.Id },
                        { "sourcePayloadRevision", source.PayloadRevision },
                        { "sourcePayloadHash", source.PayloadHash ?? string.Empty },
                        { "sourceLifecycleRevision", source.LifecycleRevision },
                        {
                            "sourceLifecycleStatus",
                            source.Status.ToString().ToUpperInvariant()
                        },
                        { "reason", plan.Reason }
                    };
                    var auditSnapshotHash =
                        ComputeDocumentHash(auditSnapshot);
                    var effectiveActorUserId =
                        ObjectId.TryParse(actorUserId, out _)
                            ? actorUserId
                            : provenance.CreatedByUserId;
                    var intent = new DynamicFlowMappingReconcileIntent
                    {
                        SchemaVersion = "P7-MAP-INVALIDATE-1",
                        ActorUserId = effectiveActorUserId,
                        ReceiptId = provenance.ReceiptId,
                        ProvenanceId = provenance.Id,
                        EventId = plan.EventId,
                        TargetReportId = provenance.TargetReportId,
                        TargetAssignmentId =
                            provenance.TargetAssignmentId,
                        CommandId = plan.InvalidatedByEventId,
                        TargetPayloadRevision =
                            provenance.TargetPayloadRevision,
                        TargetPayloadHash =
                            provenance.TargetPayloadHash,
                        TargetLifecycleRevision =
                            provenance.TargetLifecycleRevision,
                        SourceSignatureVersion =
                            provenance.SourceSignatureVersion,
                        SourceSignature = provenance.SourceSignature,
                        ResultSemanticHash =
                            provenance.ResultSemanticHash,
                        MappingRuleSetHash =
                            provenance.MappingRuleSetHash,
                        ProvenanceHash = provenance.ProvenanceHash,
                        RuntimePin = provenance.RuntimePin,
                        InvalidatedProvenanceIds = [provenance.Id],
                        InvalidatedByEventId =
                            plan.InvalidatedByEventId,
                        InvalidationReason = plan.Reason,
                        SourceReportId = source.Id,
                        RebuildOnly = true,
                        P8ExecutionEnabled = false,
                        P9ExecutionEnabled = false,
                        ProjectionSnapshot = projectionSnapshot,
                        ProjectionSnapshotHash =
                            projectionSnapshotHash,
                        AuditSnapshot = auditSnapshot,
                        AuditSnapshotHash = auditSnapshotHash,
                        ProjectionBusinessKeys =
                        [
                            $"report:{provenance.TargetReportId}",
                            $"source-report:{source.Id}",
                            $"provenance:{provenance.Id}"
                        ],
                        CommittedAtUtc = invalidatedAtUtc
                    };
                    var intentHash =
                        ComputeDocumentHash(intent.ToBsonDocument());
                    var eventPayload = new BsonDocument
                    {
                        { "schemaVersion", "P7-MAP-INVALIDATION-EVENT-1" },
                        { "outboxId", plan.OutboxId },
                        { "invalidatedByEventId", plan.InvalidatedByEventId },
                        { "reason", plan.Reason },
                        { "intentHash", intentHash }
                    };
                    var eventPayloadHash =
                        ComputeDocumentHash(eventPayload);
                    await ctx.DynamicFlowMappingEvents.InsertOneAsync(
                        session,
                        new DynamicFlowMappingEvent
                        {
                            Id = plan.EventId,
                            EventKey = plan.EventKey,
                            EventType =
                                DynamicFlowMappingEventTypes
                                    .ProvenanceInvalidated,
                            ReceiptId = provenance.ReceiptId,
                            ProvenanceId = provenance.Id,
                            TargetReportId =
                                provenance.TargetReportId,
                            TargetAssignmentId =
                                provenance.TargetAssignmentId,
                            CommandId = plan.InvalidatedByEventId,
                            CorrelationId =
                                plan.InvalidatedByEventId,
                            RuntimePin = provenance.RuntimePin,
                            TargetPayloadRevision =
                                provenance.TargetPayloadRevision,
                            TargetPayloadHash =
                                provenance.TargetPayloadHash,
                            TargetLifecycleRevision =
                                provenance.TargetLifecycleRevision,
                            SourceSignatureVersion =
                                provenance.SourceSignatureVersion,
                            SourceSignature =
                                provenance.SourceSignature,
                            ResultSemanticHash =
                                provenance.ResultSemanticHash,
                            ProvenanceHash =
                                provenance.ProvenanceHash,
                            Payload = eventPayload,
                            PayloadHash = eventPayloadHash,
                            ActorUserId = effectiveActorUserId,
                            OccurredAtUtc = invalidatedAtUtc
                        },
                        cancellationToken: transactionCt);
                    await ctx.DynamicFlowMappingOutbox.InsertOneAsync(
                        session,
                        new DynamicFlowMappingOutboxItem
                        {
                            Id = plan.OutboxId,
                            ReceiptId = provenance.ReceiptId,
                            EventId = plan.EventId,
                            ProvenanceId = provenance.Id,
                            TargetReportId =
                                provenance.TargetReportId,
                            CommandId = plan.InvalidatedByEventId,
                            Operation = RebuildIntentOperation,
                            DedupeKey = plan.OutboxDedupeKey,
                            Intent = intent,
                            IntentHash = intentHash,
                            State =
                                DynamicFlowMappingOutboxStates.Reconciled,
                            AttemptCount = 0,
                            RepairEpoch = 0,
                            NextAttemptAtUtc = invalidatedAtUtc,
                            CreatedAtUtc = invalidatedAtUtc,
                            UpdatedAtUtc = invalidatedAtUtc,
                            ReconciledAtUtc = invalidatedAtUtc
                        },
                        cancellationToken: transactionCt);
                    invalidatedIds.Add(provenance.Id);
                }

                if (affectedWorkIds.Count > 0)
                {
                    await WorkDirectSourceRevisionFence.IncrementAsync(
                        ctx,
                        session,
                        affectedWorkIds,
                        transactionCt);
                }

                return invalidatedIds;
            },
            ct);
    }

    public static bool IsHistoricalState(string? state)
        => string.Equals(
               state,
               DynamicFlowMappingProvenanceStates.Invalidated,
               StringComparison.Ordinal) ||
           string.Equals(
               state,
               DynamicFlowMappingProvenanceStates.Superseded,
               StringComparison.Ordinal);

    public static bool IsStateAllowed(
        string? state,
        DynamicFlowMappingIntegrityMode mode)
        => string.Equals(
               state,
               DynamicFlowMappingProvenanceStates.Current,
               StringComparison.Ordinal) ||
           mode == DynamicFlowMappingIntegrityMode.AllowHistorical &&
           IsHistoricalState(state);

    public static bool AllowsHistoricalLifecycleTransition(
        string? operation)
        => operation?.Trim().ToUpperInvariant() is
            "LEGACY_RETURN" or
            "WITHDRAW" or
            "REVIEW_RETURN" or
            "REVIEW_RECALL_APPROVED" or
            "REVIEW_DEACTIVATE_REPORT" or
            "AUTO_AGGREGATE_REVIEW_INVALIDATED";

    public static string ResolveSourceDriftReason(
        DynamicFlowMappingSourcePin pin,
        WorkAssignmentReport source)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(source);
        return pin.SourcePayloadRevision != source.PayloadRevision ||
               !string.Equals(
                   pin.SourcePayloadHash,
                   source.PayloadHash,
                   StringComparison.Ordinal)
            ? SourcePayloadDriftReason
            : SourceLifecycleDriftReason;
    }

    public static string ComputeDocumentHash(BsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(
            document.ToJson(
                new JsonWriterSettings
                {
                    OutputMode = JsonOutputMode.RelaxedExtendedJson
                }));
    }

    public static string ComputeCanonicalHash(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(
            JsonSerializer.Serialize(value, JsonOptions));
    }

    public static string ComputeStableObjectId(string value)
    {
        value = Required(value, nameof(value));
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24]
            .ToLowerInvariant();
    }

    public static bool IsLowerSha256(string? value)
        => value is { Length: 64 } &&
           value.All(character =>
               character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ComputeSourceFactHash(
        DynamicFlowMappingSourcePin pin)
        => ComputeCanonicalHash(
            new
            {
                sourceReportId = pin.SourceReportId,
                sourceAssignmentId = pin.SourceAssignmentId,
                sourceFlowInstanceId = pin.SourceFlowInstanceId,
                sourceExecutionEpoch = pin.SourceExecutionEpoch,
                sourceStepInstanceId = pin.SourceStepInstanceId,
                sourceStepId = pin.SourceStepId,
                sourceBranchId = pin.SourceBranchId,
                sourceAttemptNo = pin.SourceAttemptNo,
                sourceFormFamilyId = pin.SourceFormFamilyId,
                sourceFormVersionId = pin.SourceFormVersionId,
                sourceFormVersionNo = pin.SourceFormVersionNo,
                sourceFormSchemaHash = pin.SourceFormSchemaHash,
                sourcePayloadRevision = pin.SourcePayloadRevision,
                sourcePayloadHash = pin.SourcePayloadHash,
                sourceLifecycleRevision = pin.SourceLifecycleRevision,
                sourceLifecycleStatus = pin.SourceLifecycleStatus,
                sourcePeriodInstanceKey = pin.SourcePeriodInstanceKey
            });

    private static bool IsMappingSummary(string? summarySourceJson)
    {
        if (string.IsNullOrWhiteSpace(summarySourceJson))
            return false;
        try
        {
            using var document = JsonDocument.Parse(summarySourceJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(
                       "kind",
                       out var kind) &&
                   string.Equals(
                       kind.GetString(),
                       "DYNAMIC_FLOW_MAPPING",
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static AppException ProvenanceTampered(
        WorkAssignmentReport report,
        string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_MAPPING_PROVENANCE_TAMPERED,
            new
            {
                reportId = report.Id,
                receiptId = report.DynamicFlowMappingReceiptId,
                provenanceId = report.DynamicFlowMappingProvenanceId,
                resultPayloadRevision =
                    report.DynamicFlowMappingResultPayloadRevision,
                reason
            });

    private static string Required(string? value, string parameterName)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required.", parameterName)
            : value;
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}
