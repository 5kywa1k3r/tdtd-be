namespace tdtd_be.DTOs.DynamicForms;

public sealed record DynamicFormSectionSourceQuery(string? Q = null, int Page = 0, int PageSize = 20, string? ExcludeFormId = null);
public sealed record DynamicFormSectionSourceRow(string Id, string Code, string Name, int VersionNo,
    int Revision, bool IsPublished, string CreatedByUsername);
