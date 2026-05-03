using System.Text.Json;

namespace tdtd_be.Services.WorkAssignments.Aggregate;

public interface IReportTemplateRuntimeTypeResolver
{
    string Resolve(object? workbook);
}

public sealed class ReportTemplateRuntimeTypeResolver : IReportTemplateRuntimeTypeResolver
{
    public string Resolve(object? workbook)
    {
        if (workbook == null)
            return "FORM_1D";

        if (workbook is Dictionary<string, object?> dict)
        {
            if (dict.TryGetValue("templateRuntimeType", out var typeObj) && typeObj is string typeText && !string.IsNullOrWhiteSpace(typeText))
                return typeText.ToUpperInvariant();
        }

        if (workbook is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            if (je.TryGetProperty("templateRuntimeType", out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString()?.ToUpperInvariant() ?? "FORM_1D";
        }

        return "FORM_1D";
    }
}