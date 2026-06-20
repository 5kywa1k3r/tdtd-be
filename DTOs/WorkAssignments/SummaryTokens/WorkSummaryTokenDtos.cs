using tdtd_be.Models;

namespace tdtd_be.DTOs.WorkAssignments.SummaryTokens;

public sealed class WorkSummaryTokenGrantRequest
{
    public string OwnerUserId { get; set; } = string.Empty;
    public int Units { get; set; }
    public string? TokenKind { get; set; }
    public string? PeriodMonthKey { get; set; }
    public string? Reason { get; set; }
}

public sealed class WorkSummaryTokenLedgerSearchRequest
{
    public string? OwnerUserId { get; set; }
    public string? ActorUserId { get; set; }
    public string? IssuerUserId { get; set; }
    public string? TokenKind { get; set; }
    public string? Direction { get; set; }
    public string? Outcome { get; set; }
    public string? PeriodMonthKey { get; set; }
    public string? ConfigId { get; set; }
    public string? JobId { get; set; }
    public string? Query { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; } = 50;
}

public sealed class WorkSummaryTokenQuotaResponse
{
    public string OwnerUserId { get; set; } = string.Empty;
    public string TokenKind { get; set; } = WorkSummaryTokenKinds.AdvancedSummaryConfigLock;
    public string PeriodMonthKey { get; set; } = string.Empty;
    public int BaseMonthlyQuota { get; set; }
    public int GrantedUnits { get; set; }
    public int UsedUnits { get; set; }
    public int MonthlyQuota { get; set; }
    public int RemainingUnits { get; set; }
}

public sealed class WorkSummaryTokenGrantResponse
{
    public string LedgerId { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public string IssuerUserId { get; set; } = string.Empty;
    public string TokenKind { get; set; } = WorkSummaryTokenKinds.AdvancedSummaryConfigLock;
    public string PeriodMonthKey { get; set; } = string.Empty;
    public int Units { get; set; }
    public WorkSummaryTokenQuotaResponse Quota { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class WorkSummaryTokenLedgerRow
{
    public string Id { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string? IssuerUserId { get; set; }
    public string TokenKind { get; set; } = WorkSummaryTokenKinds.AdvancedSummaryConfigLock;
    public string Direction { get; set; } = WorkSummaryTokenDirections.Consume;
    public int Units { get; set; }
    public int MonthlyQuota { get; set; }
    public string PeriodMonthKey { get; set; } = string.Empty;
    public string? RequestTokenId { get; set; }
    public string? WorkId { get; set; }
    public string? WorkAssignmentId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? SectionId { get; set; }
    public string? ConfigId { get; set; }
    public int? ConfigVersionNo { get; set; }
    public string? ConfigHash { get; set; }
    public string? JobId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Outcome { get; set; } = WorkSummaryTokenOutcomes.Success;
    public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
