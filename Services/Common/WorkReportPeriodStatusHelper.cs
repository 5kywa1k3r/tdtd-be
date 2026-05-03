using tdtd_be.Models.Enums;

namespace tdtd_be.Services.Common;

public static class WorkReportPeriodStatusHelper
{
    public const string BucketAll = "ALL";
    public const string BucketPending = "PENDING";
    public const string BucketDraft = "DRAFT";
    public const string BucketSubmitted = "SUBMITTED";
    public const string BucketApproved = "APPROVED";
    public const string BucketOverdue = "OVERDUE";
    public const string BucketReturned = "RETURNED";

    public static IReadOnlyCollection<WorkReportPeriodStatus> TerminalStatuses { get; } =
        new[] { WorkReportPeriodStatus.Approved, WorkReportPeriodStatus.OverdueApproved };

    public static IReadOnlyCollection<WorkReportPeriodStatus> OverdueStatuses { get; } =
        new[]
        {
            WorkReportPeriodStatus.OverduePending,
            WorkReportPeriodStatus.OverdueDraft,
            WorkReportPeriodStatus.OverdueSubmitted,
            WorkReportPeriodStatus.OverdueApproved
        };

    public static bool IsOverdue(WorkReportPeriodStatus status)
        => status is WorkReportPeriodStatus.OverduePending
            or WorkReportPeriodStatus.OverdueDraft
            or WorkReportPeriodStatus.OverdueSubmitted
            or WorkReportPeriodStatus.OverdueApproved;

    public static bool IsWaitingReview(WorkReportPeriodStatus status)
        => status is WorkReportPeriodStatus.Submitted or WorkReportPeriodStatus.OverdueSubmitted;

    public static bool IsTerminal(WorkReportPeriodStatus status)
        => status is WorkReportPeriodStatus.Approved or WorkReportPeriodStatus.OverdueApproved;

    public static bool ShouldKeepQueueActive(WorkReportPeriodStatus status)
        => status is WorkReportPeriodStatus.Pending
            or WorkReportPeriodStatus.Draft
            or WorkReportPeriodStatus.Submitted;

    public static WorkReportPeriodStatus ResolveInitialStatus(DateTime? dueAtUtc, DateTime nowUtc)
        => dueAtUtc.HasValue && nowUtc > dueAtUtc.Value
            ? WorkReportPeriodStatus.OverduePending
            : WorkReportPeriodStatus.Pending;

    public static WorkReportPeriodStatus ResolveDraftStatus(DateTime? dueAtUtc, DateTime nowUtc)
        => dueAtUtc.HasValue && nowUtc > dueAtUtc.Value
            ? WorkReportPeriodStatus.OverdueDraft
            : WorkReportPeriodStatus.Draft;

    public static WorkReportPeriodStatus ResolveSubmittedStatus(DateTime? dueAtUtc, DateTime nowUtc)
        => dueAtUtc.HasValue && nowUtc > dueAtUtc.Value
            ? WorkReportPeriodStatus.OverdueSubmitted
            : WorkReportPeriodStatus.Submitted;

    public static WorkReportPeriodStatus ResolveApprovedStatus(
        WorkReportPeriodStatus currentStatus,
        DateTime? dueAtUtc,
        bool isLateSubmission,
        DateTime nowUtc)
    {
        var isOverdueApproved =
            isLateSubmission ||
            IsOverdue(currentStatus) ||
            (dueAtUtc.HasValue && nowUtc > dueAtUtc.Value);

        return isOverdueApproved
            ? WorkReportPeriodStatus.OverdueApproved
            : WorkReportPeriodStatus.Approved;
    }

    public static WorkReportPeriodStatus ResolveDueScanStatus(
        WorkReportPeriodStatus currentStatus,
        bool currentIsOverdue,
        DateTime? dueAtUtc,
        DateTime nowUtc)
    {
        if (!dueAtUtc.HasValue || nowUtc <= dueAtUtc.Value)
            return currentStatus;

        return currentStatus switch
        {
            WorkReportPeriodStatus.Pending => WorkReportPeriodStatus.OverduePending,
            WorkReportPeriodStatus.Draft => WorkReportPeriodStatus.OverdueDraft,
            WorkReportPeriodStatus.Submitted => WorkReportPeriodStatus.OverdueSubmitted,
            WorkReportPeriodStatus.Approved when currentIsOverdue => WorkReportPeriodStatus.OverdueApproved,
            _ => currentStatus
        };
    }

    public static bool IsReturned(
        WorkReportPeriodStatus status,
        string? periodReturnReason,
        string? reportReturnReason,
        DateTime? reportReturnedAtUtc)
    {
        var hasReturnSignal =
            reportReturnedAtUtc.HasValue ||
            !string.IsNullOrWhiteSpace(periodReturnReason) ||
            !string.IsNullOrWhiteSpace(reportReturnReason);

        return hasReturnSignal &&
               status is WorkReportPeriodStatus.Draft or WorkReportPeriodStatus.OverdueDraft;
    }

    public static string NormalizeReviewBucket(string? bucket)
    {
        var normalized = (bucket ?? string.Empty).Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? BucketAll : normalized;
    }

    public static bool ShouldFilterReviewBucket(string? bucket)
    {
        var normalized = NormalizeReviewBucket(bucket);
        return normalized != BucketAll;
    }

    public static string ToReviewStatusBucket(
        WorkReportPeriodStatus status,
        string? periodReturnReason,
        string? reportReturnReason,
        DateTime? reportReturnedAtUtc)
    {
        if (IsReturned(status, periodReturnReason, reportReturnReason, reportReturnedAtUtc))
            return BucketReturned;

        if (IsOverdue(status))
            return BucketOverdue;

        return status switch
        {
            WorkReportPeriodStatus.Pending => BucketPending,
            WorkReportPeriodStatus.Draft => BucketDraft,
            WorkReportPeriodStatus.Submitted => BucketSubmitted,
            WorkReportPeriodStatus.Approved => BucketApproved,
            _ => BucketAll
        };
    }

    public static int GetPeriodRiskRank(WorkReportPeriodStatus status)
    {
        return status switch
        {
            WorkReportPeriodStatus.OverdueSubmitted => 7,
            WorkReportPeriodStatus.OverdueDraft => 6,
            WorkReportPeriodStatus.OverduePending => 5,
            WorkReportPeriodStatus.Submitted => 4,
            WorkReportPeriodStatus.Draft => 3,
            WorkReportPeriodStatus.Pending => 2,
            WorkReportPeriodStatus.OverdueApproved => 1,
            WorkReportPeriodStatus.Approved => 0,
            _ => -1
        };
    }

    public static int GetReviewRank(WorkReportPeriodStatus status)
        => GetPeriodRiskRank(status);
}
