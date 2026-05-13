namespace tdtd_be.DTOs.AdminImport;

public sealed record ImportTemplateFile(
    byte[] Content,
    string ContentType,
    string FileName
);

public sealed record ImportRowError(
    int RowNumber,
    string Field,
    string Code,
    string Message
);

public sealed record ImportResult(
    int TotalRows,
    int ValidRows,
    int ErrorRows,
    IReadOnlyList<ImportRowError> Errors,
    IReadOnlyList<string> CreatedIds
)
{
    public IReadOnlyList<ImportPreviewRow> Rows { get; init; } = Array.Empty<ImportPreviewRow>();
}

public sealed record ImportPreviewRow(
    int RowNumber,
    string? ExternalKey,
    string? FullName,
    string? ParentCode,
    string? GeneratedCode,
    string? CreatedId = null
);
