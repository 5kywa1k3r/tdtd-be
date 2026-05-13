namespace tdtd_be.DTOs.WorkDocuments;

public sealed class WorkDocumentRow
{
    public string Id { get; set; } = default!;
    public string OriginalName { get; set; } = default!;
    public string MimeType { get; set; } = default!;
    public long Size { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string Scope { get; set; } = default!;
    public string SourceType { get; set; } = default!;
    public string? WorkId { get; set; }
    public string? AssignmentId { get; set; }
    public string? AssignmentCode { get; set; }
    public string? AssignmentPath { get; set; }
    public string? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }
    public bool CanDelete { get; set; }
}

public sealed class WorkDocumentUploadTarget
{
    public string AssignmentId { get; set; } = default!;
    public string Code { get; set; } = default!;
    public string Path { get; set; } = default!;
    public string Label { get; set; } = default!;
}

public sealed class WorkDocumentUploadOptions
{
    public bool CanUploadWork { get; set; }
    public List<WorkDocumentUploadTarget> AssignmentTargets { get; set; } = new();
}

public sealed class CreateWorkDocumentUploadSessionReq
{
    public string FileName { get; set; } = default!;
    public long Size { get; set; }
    public string? Mime { get; set; }
}

public sealed class CreateWorkDocumentUploadSessionResp
{
    public string Endpoint { get; set; } = default!;
    public string UploadToken { get; set; } = default!;
    public long ChunkSize { get; set; }
    public long MaxSize { get; set; }
}
