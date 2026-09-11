using System.Text.Json.Serialization;
using tdtd_be.Models;

namespace tdtd_be.DTOs.WorkAssignments.SummaryTokens;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkSummaryTokenGrantP8Payload(
    int Units,
    string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkSummaryTokenCompensationP8Payload(
    string? Reason);

public sealed class WorkSummaryTokenGrantRequest
{
    public string OwnerUnitId { get; set; } = string.Empty;
    public int Units { get; set; }
    public string? TokenKind { get; set; }
    public string? PeriodMonthKey { get; set; }
    public string? Reason { get; set; }
}

public sealed class WorkSummaryTokenLedgerSearchRequest
{
    public string? OwnerUnitId { get; set; }
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
    public string PoolId { get; set; } = string.Empty;
    public long Revision { get; set; }
    public string PoolHash { get; set; } = string.Empty;
    public string OwnerUnitId { get; set; } = string.Empty;
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
    public string CommandReceiptId { get; set; } = string.Empty;
    public string OwnerUnitId { get; set; } = string.Empty;
    public string IssuerUserId { get; set; } = string.Empty;
    public string TokenKind { get; set; } = WorkSummaryTokenKinds.AdvancedSummaryConfigLock;
    public string PeriodMonthKey { get; set; } = string.Empty;
    public int Units { get; set; }
    public long PoolRevision { get; set; }
    public string PoolHash { get; set; } = string.Empty;
    public WorkSummaryTokenQuotaResponse Quota { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class WorkSummaryTokenCompensationResponse
{
    public string CompensationLedgerId { get; set; } = string.Empty;
    public string CommandReceiptId { get; set; } = string.Empty;
    public string CompensatedLedgerId { get; set; } = string.Empty;
    public string OwnerUnitId { get; set; } = string.Empty;
    public string TokenKind { get; set; } =
        WorkSummaryTokenKinds.AdvancedSummaryConfigLock;
    public string PeriodMonthKey { get; set; } = string.Empty;
    public int Units { get; set; }
    public long PoolRevision { get; set; }
    public string PoolHash { get; set; } = string.Empty;
    public WorkSummaryTokenQuotaResponse Quota { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class WorkSummaryTokenLedgerRow
{
    public string Id { get; set; } = string.Empty;
    public string RecordKind { get; set; } =
        WorkSummaryTokenLedgerRecordKinds.Entry;
    public string? OwnerUserId { get; set; }
    public string? OwnerUnitId { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string? IssuerUserId { get; set; }
    public string TokenKind { get; set; } = WorkSummaryTokenKinds.AdvancedSummaryConfigLock;
    public string Direction { get; set; } = WorkSummaryTokenDirections.Consume;
    public int Units { get; set; }
    public int MonthlyQuota { get; set; }
    public int BaseMonthlyQuota { get; set; }
    public int GrantedUnits { get; set; }
    public int UsedUnits { get; set; }
    public long Revision { get; set; }
    public string PeriodMonthKey { get; set; } = string.Empty;
    public string? RequestTokenId { get; set; }
    public string? RequestHash { get; set; }
    public string? WorkId { get; set; }
    public string? WorkAssignmentId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? SectionId { get; set; }
    public string? ConfigId { get; set; }
    public int? ConfigVersionNo { get; set; }
    public string? ConfigHash { get; set; }
    public string? PoolId { get; set; }
    public long? PoolRevisionBefore { get; set; }
    public long? PoolRevisionAfter { get; set; }
    public string? PoolHashBefore { get; set; }
    public string? PoolHashAfter { get; set; }
    public string? CommandReceiptId { get; set; }
    public string? CompensatesLedgerId { get; set; }
    public string? JobId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Outcome { get; set; } = WorkSummaryTokenOutcomes.Success;
    public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
