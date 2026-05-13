namespace tdtd_be.Services.WorkAssignments.Lookups;

public interface IWorkAssignmentTemplateResolver
{
    Task<WorkAssignmentTemplateResolution> ResolveAsync(
        string? dynamicFormTemplateId,
        CancellationToken ct = default);
}

public sealed record WorkAssignmentTemplateResolution(
    string? DynamicFormTemplateId,
    string? DynamicFormTemplateCode,
    string? DynamicFormTemplateName,
    string? DynamicExcelId,
    string DynamicExcelCode,
    string DynamicExcelName);
