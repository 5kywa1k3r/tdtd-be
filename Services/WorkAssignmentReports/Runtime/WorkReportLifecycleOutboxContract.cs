using System.Security.Cryptography;
using System.Text;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.Services.WorkAssignmentReports.Runtime;

/// <summary>
/// Immutable input for a durable lifecycle projection entry. Callers compose the returned
/// update with their existing lifecycle CAS so there is no committed lifecycle without a
/// recoverable projection record.
/// </summary>
public sealed record WorkReportLifecycleOutboxSeed(
    string CommandId,
    int LifecycleRevision,
    string Operation,
    string? ActorUserId,
    string FromStatus,
    string ToStatus,
    bool FromIsActive,
    bool ToIsActive,
    int PayloadRevision,
    string? PayloadHash,
    DateTime CreatedAtUtc,
    string? BusinessReason = null,
    string? BusinessComment = null,
    string? SecondaryBusinessReason = null,
    string? SecondaryBusinessComment = null,
    string? BusinessSnapshotJson = null,
    DynamicFlowMappingLifecycleBinding? MappingBinding = null);

public static class WorkReportLifecycleOutboxContract
{
    public static WorkReportLifecycleOutboxSeed FromCommand(
        WorkAssignmentReport report,
        WorkReportLifecycleCommand command,
        WorkAssignmentReportStatus resultStatus,
        bool resultIsActive,
        string? actorUserId,
        DateTime committedAtUtc,
        string? resultPayloadHash = null,
        string? businessReason = null,
        string? businessComment = null,
        string? secondaryBusinessReason = null,
        string? secondaryBusinessComment = null,
        string? businessSnapshotJson = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(command);

        return new WorkReportLifecycleOutboxSeed(
            command.CommandId,
            command.ExpectedLifecycleRevision + 1,
            command.Operation,
            NormalizeOptional(actorUserId),
            report.Status.ToString(),
            resultStatus.ToString(),
            report.IsActive,
            resultIsActive,
            command.ExpectedPayloadRevision + command.PayloadRevisionDelta,
            NormalizeOptional(resultPayloadHash) ?? NormalizeOptional(report.PayloadHash),
            EnsureUtc(committedAtUtc),
            NormalizeOptional(businessReason),
            NormalizeOptional(businessComment),
            NormalizeOptional(secondaryBusinessReason),
            NormalizeOptional(secondaryBusinessComment),
            NormalizeOptional(businessSnapshotJson),
            DynamicFlowMappingLifecycleBinding.FromReport(report));
    }

    public static UpdateDefinition<WorkAssignmentReport> Append(
        UpdateDefinition<WorkAssignmentReport> update,
        WorkReportLifecycleOutboxSeed seed)
    {
        ArgumentNullException.ThrowIfNull(update);
        var entry = CreateEntry(seed);

        return update
            .Push(x => x.LifecycleProjectionOutbox, entry)
            .Set(x => x.LifecycleProjectionLastError, null);
    }

    public static WorkReportLifecycleProjectionOutboxEntry CreateEntry(
        WorkReportLifecycleOutboxSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var commandId = Required(seed.CommandId, nameof(seed.CommandId));
        var operation = Required(seed.Operation, nameof(seed.Operation)).ToUpperInvariant();
        var fromStatus = Required(seed.FromStatus, nameof(seed.FromStatus)).ToUpperInvariant();
        var toStatus = Required(seed.ToStatus, nameof(seed.ToStatus)).ToUpperInvariant();
        if (seed.LifecycleRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(seed.LifecycleRevision));
        if (seed.PayloadRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(seed.PayloadRevision));

        var entryKey = ComputeEntryKey(commandId, seed.LifecycleRevision, operation);
        return new WorkReportLifecycleProjectionOutboxEntry
        {
            EntryKey = entryKey,
            CommandId = commandId,
            LifecycleRevision = seed.LifecycleRevision,
            Operation = operation,
            State = WorkReportLifecycleProjectionOutboxStates.Pending,
            ActorUserId = NormalizeOptional(seed.ActorUserId),
            FromStatus = fromStatus,
            ToStatus = toStatus,
            FromIsActive = seed.FromIsActive,
            ToIsActive = seed.ToIsActive,
            PayloadRevision = seed.PayloadRevision,
            PayloadHash = NormalizeOptional(seed.PayloadHash),
            DynamicFlowMappingReceiptId =
                NormalizeOptional(seed.MappingBinding?.ReceiptId),
            DynamicFlowMappingProvenanceId =
                NormalizeOptional(seed.MappingBinding?.ProvenanceId),
            DynamicFlowMappingProvenanceHash =
                NormalizeOptional(seed.MappingBinding?.ProvenanceHash),
            DynamicFlowMappingResultPayloadRevision =
                seed.MappingBinding?.ResultPayloadRevision,
            DynamicFlowMappingResultPayloadHash =
                NormalizeOptional(seed.MappingBinding?.ResultPayloadHash),
            CreatedAtUtc = EnsureUtc(seed.CreatedAtUtc),
            AttemptCount = 0,
            BusinessEvents = CreateBusinessEvents(seed, entryKey, operation, fromStatus, toStatus)
        };
    }

    /// <summary>
    /// Stable event keys are shared by foreground projection and retry projection. The key is
    /// independent of attempt time and becomes the unique identity in each business-log store.
    /// </summary>
    public static string ComputeBusinessEventKey(string entryKey, int ordinal)
    {
        entryKey = Required(entryKey, nameof(entryKey));
        if (ordinal < 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        var canonical = $"{entryKey}\nBUSINESS_EVENT\n{ordinal}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public static string ComputeStableObjectId(string eventKey)
    {
        eventKey = Required(eventKey, nameof(eventKey));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventKey)))[..24]
            .ToLowerInvariant();
    }

    /// <summary>
    /// Backward-compatible projection for entries written before businessEvents was introduced.
    /// New entries persist the returned immutable event list in the lifecycle CAS itself.
    /// </summary>
    public static IReadOnlyList<WorkReportLifecycleBusinessEvent> ResolveBusinessEvents(
        WorkReportLifecycleProjectionOutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.BusinessEvents is { Count: > 0 }
            ? entry.BusinessEvents
            : CreateBusinessEvents(
                new WorkReportLifecycleOutboxSeed(
                    entry.CommandId,
                    entry.LifecycleRevision,
                    entry.Operation,
                    entry.ActorUserId,
                    entry.FromStatus,
                    entry.ToStatus,
                    entry.FromIsActive,
                    entry.ToIsActive,
                    entry.PayloadRevision,
                    entry.PayloadHash,
                    entry.CreatedAtUtc,
                    MappingBinding:
                        ResolveMappingBinding(entry)),
                entry.EntryKey,
                Required(entry.Operation, nameof(entry.Operation)).ToUpperInvariant(),
                Required(entry.FromStatus, nameof(entry.FromStatus)).ToUpperInvariant(),
                Required(entry.ToStatus, nameof(entry.ToStatus)).ToUpperInvariant());
    }

    private static List<WorkReportLifecycleBusinessEvent> CreateBusinessEvents(
        WorkReportLifecycleOutboxSeed seed,
        string entryKey,
        string operation,
        string fromStatus,
        string toStatus)
    {
        var events = new List<WorkReportLifecycleBusinessEvent>();
        var statusData = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fromStatus"] = fromStatus,
            ["toStatus"] = toStatus,
            ["fromActive"] = seed.FromIsActive.ToString(),
            ["toActive"] = seed.ToIsActive.ToString(),
            ["lifecycleRevision"] = seed.LifecycleRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (seed.MappingBinding is not null)
        {
            statusData["dynamicFlowMappingReceiptId"] =
                seed.MappingBinding.ReceiptId;
            statusData["dynamicFlowMappingProvenanceId"] =
                seed.MappingBinding.ProvenanceId;
            statusData["dynamicFlowMappingProvenanceHash"] =
                seed.MappingBinding.ProvenanceHash;
            statusData["dynamicFlowMappingResultPayloadRevision"] =
                seed.MappingBinding.ResultPayloadRevision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            statusData["dynamicFlowMappingResultPayloadHash"] =
                seed.MappingBinding.ResultPayloadHash;
        }

        void Add(
            string? reportAction,
            string? userAction,
            string? statusOperation,
            string statusScope,
            string eventFromStatus,
            string eventToStatus,
            string? reason,
            string? comment,
            string? snapshotJson,
            string? summaryVerb,
            Dictionary<string, string>? data = null)
        {
            events.Add(new WorkReportLifecycleBusinessEvent
            {
                EventKey = ComputeBusinessEventKey(entryKey, events.Count),
                ReportLogAction = NormalizeOptional(reportAction),
                UserAction = NormalizeOptional(userAction),
                StatusOperation = NormalizeOptional(statusOperation),
                StatusScope = NormalizeOptional(statusScope),
                FromStatus = Required(eventFromStatus, nameof(eventFromStatus)).ToUpperInvariant(),
                ToStatus = Required(eventToStatus, nameof(eventToStatus)).ToUpperInvariant(),
                Reason = NormalizeOptional(reason),
                Comment = NormalizeOptional(comment),
                SnapshotJson = NormalizeOptional(snapshotJson),
                SummaryVerb = NormalizeOptional(summaryVerb),
                Data = data is null
                    ? new Dictionary<string, string>(statusData, StringComparer.Ordinal)
                    : new Dictionary<string, string>(data, StringComparer.Ordinal)
            });
        }

        switch (operation)
        {
            case "SUBMIT" when string.Equals(toStatus, "APPROVED", StringComparison.Ordinal):
                Add(
                    "SUBMIT",
                    UserActionLogActions.ReportSubmitted,
                    "SUBMIT_AUTO_APPROVE",
                    "report",
                    "DRAFT",
                    "SUBMITTED",
                    null,
                    seed.BusinessComment,
                    null,
                    "Submitted report");
                Add(
                    "AUTO_APPROVE",
                    UserActionLogActions.ReportApproved,
                    null,
                    "report",
                    "SUBMITTED",
                    "APPROVED",
                    seed.SecondaryBusinessReason ?? "AUTO_APPROVE_CONDITION",
                    seed.SecondaryBusinessComment,
                    seed.BusinessSnapshotJson,
                    "Auto approved report");
                break;

            case "SUBMIT":
                Add("SUBMIT", UserActionLogActions.ReportSubmitted, "SUBMIT", "report", fromStatus, toStatus,
                    seed.BusinessReason, seed.BusinessComment, seed.BusinessSnapshotJson, "Submitted report");
                break;

            case "LEGACY_APPROVE":
                Add("APPROVE", UserActionLogActions.ReportApproved, "APPROVE", "report", fromStatus, toStatus,
                    seed.BusinessReason, seed.BusinessComment, seed.BusinessSnapshotJson, "Approved report");
                break;

            case "LEGACY_RETURN":
                Add("RETURN", UserActionLogActions.ReportReturned, "RETURN", "report", fromStatus, toStatus,
                    seed.BusinessReason, seed.BusinessComment, seed.BusinessSnapshotJson, "Returned report");
                break;

            case "WITHDRAW":
                Add("Thu hồi báo cáo", null,
                    string.Equals(fromStatus, "APPROVED", StringComparison.Ordinal)
                        ? "WITHDRAW_AUTO_APPROVED"
                        : "WITHDRAW_SUBMITTED",
                    "report", fromStatus, toStatus,
                    seed.BusinessReason, seed.BusinessComment, seed.BusinessSnapshotJson, null);
                break;

            case "REVIEW_CONFIRM_AUTO_APPROVE":
                Add("Xác nhận tự duyệt", UserActionLogActions.ReportApproved, operation, "review-report", fromStatus, toStatus,
                    seed.BusinessReason ?? "AUTO_APPROVE_CONFIRMED", seed.BusinessComment, seed.BusinessSnapshotJson,
                    "Confirmed auto approved report");
                break;

            case "REVIEW_APPROVE":
                Add("Duyệt", UserActionLogActions.ReportApproved, operation, "review-report", fromStatus, toStatus,
                    seed.BusinessReason, seed.BusinessComment, seed.BusinessSnapshotJson, "Approved report");
                break;

            case "REVIEW_RETURN":
                Add("Trả lại", UserActionLogActions.ReportReturned, operation, "review-report", fromStatus, toStatus,
                    seed.BusinessReason, seed.BusinessComment, seed.BusinessSnapshotJson, "Returned report");
                break;

            case "REVIEW_RECALL_APPROVED":
                Add("Thu hồi duyệt", null, operation, "review-report", fromStatus, toStatus,
                    seed.BusinessReason, seed.BusinessComment, seed.BusinessSnapshotJson, null);
                break;

            case "REVIEW_DEACTIVATE_REPORT":
                Add("Deactivate", UserActionLogActions.ReportDeactivated, operation, "review-report", "ACTIVE", "INACTIVE",
                    seed.BusinessReason, seed.BusinessComment, seed.BusinessSnapshotJson, "Deactivated report");
                break;

            case "REVIEW_REACTIVATE_REPORT":
                Add("Reactivate", UserActionLogActions.ReportReactivated, operation, "review-report", "INACTIVE", "ACTIVE",
                    seed.BusinessReason, seed.BusinessComment, seed.BusinessSnapshotJson, "Reactivated report");
                break;

            case "AUTO_AGGREGATE_REVIEW_INVALIDATED":
                Add(operation, null, operation, "report", fromStatus, toStatus,
                    seed.BusinessReason, seed.BusinessComment, seed.BusinessSnapshotJson, null);
                break;

            default:
                Add(operation, null, operation, "report", fromStatus, toStatus,
                    seed.BusinessReason, seed.BusinessComment, seed.BusinessSnapshotJson, null);
                break;
        }

        return events;
    }

    public static string ComputeEntryKey(string commandId, int lifecycleRevision, string operation)
    {
        commandId = Required(commandId, nameof(commandId));
        operation = Required(operation, nameof(operation)).ToUpperInvariant();
        if (lifecycleRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(lifecycleRevision));

        var canonical = $"{commandId}\n{lifecycleRevision}\n{operation}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static string Required(string? value, string parameterName)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required.", parameterName)
            : value;
    }

    private static string? NormalizeOptional(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static DynamicFlowMappingLifecycleBinding? ResolveMappingBinding(
        WorkReportLifecycleProjectionOutboxEntry entry)
    {
        var hasAny =
            !string.IsNullOrWhiteSpace(
                entry.DynamicFlowMappingReceiptId) ||
            !string.IsNullOrWhiteSpace(
                entry.DynamicFlowMappingProvenanceId) ||
            !string.IsNullOrWhiteSpace(
                entry.DynamicFlowMappingProvenanceHash) ||
            entry.DynamicFlowMappingResultPayloadRevision.HasValue ||
            !string.IsNullOrWhiteSpace(
                entry.DynamicFlowMappingResultPayloadHash);
        if (!hasAny)
            return null;
        if (string.IsNullOrWhiteSpace(
                entry.DynamicFlowMappingReceiptId) ||
            string.IsNullOrWhiteSpace(
                entry.DynamicFlowMappingProvenanceId) ||
            !DynamicFlowMappingLifecycleContract.IsLowerSha256(
                entry.DynamicFlowMappingProvenanceHash) ||
            entry.DynamicFlowMappingResultPayloadRevision is not > 0 ||
            !DynamicFlowMappingLifecycleContract.IsLowerSha256(
                entry.DynamicFlowMappingResultPayloadHash))
        {
            throw new InvalidOperationException(
                DynamicFlowMappingLifecycleContract
                    .HeaderReferenceIncompleteReason);
        }

        return new DynamicFlowMappingLifecycleBinding(
            entry.DynamicFlowMappingReceiptId.Trim(),
            entry.DynamicFlowMappingProvenanceId.Trim(),
            entry.DynamicFlowMappingProvenanceHash!,
            entry.DynamicFlowMappingResultPayloadRevision.Value,
            entry.DynamicFlowMappingResultPayloadHash!);
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}
