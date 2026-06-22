namespace tdtd_be.DTOs.DynamicFlows;

public sealed class DynamicFlowTemplateSearchRequest
{
    public string? Query { get; set; }
    public string? Status { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; } = 20;
}

public sealed class DynamicFlowTemplateDto
{
    public string Id { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = "DRAFT";
    public string? CurrentVersionId { get; set; }
    public int? CurrentVersionNo { get; set; }
    public string? CurrentVersionHash { get; set; }
    public DynamicFlowTemplateVersionDto? CurrentVersion { get; set; }
    public DynamicFlowTemplateVersionDto? DraftVersion { get; set; }
    public List<DynamicFlowTemplateVersionDto> Versions { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class DynamicFlowTemplateVersionDto
{
    public string Id { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public int VersionNo { get; set; }
    public string Status { get; set; } = "DRAFT";
    public int DraftRevision { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string PayloadHash { get; set; } = string.Empty;
    public bool IsUsed { get; set; }
    public DateTime? LockedAtUtc { get; set; }
    public string? LockedByUserId { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public string? ArchivedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class CreateDynamicFlowTemplateRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? PayloadJson { get; set; }
}

public sealed class UpdateDynamicFlowTemplateRequest
{
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
}

public sealed class SaveDynamicFlowTemplateVersionDraftRequest
{
    public string PayloadJson { get; set; } = "{}";
}

public sealed class LockDynamicFlowTemplateVersionRequest
{
}
