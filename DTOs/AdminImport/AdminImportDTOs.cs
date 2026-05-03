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
);
