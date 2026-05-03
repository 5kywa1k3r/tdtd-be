using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.DynamicForms;

namespace tdtd_be.Services.WorkAssignments.Lookups;

public sealed class WorkAssignmentTemplateResolver : IWorkAssignmentTemplateResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MongoDbContext _ctx;

    public WorkAssignmentTemplateResolver(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<WorkAssignmentTemplateResolution> ResolveAsync(
        string? dynamicFormTemplateId,
        string? dynamicExcelId,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            return await ResolveDynamicFormAsync(dynamicFormTemplateId.Trim(), ct);

        if (!string.IsNullOrWhiteSpace(dynamicExcelId))
            return await ResolveLegacyDynamicExcelAsync(dynamicExcelId.Trim(), ct);

        throw new InvalidOperationException("Thiếu Dynamic Form template.");
    }

    private async Task<WorkAssignmentTemplateResolution> ResolveDynamicFormAsync(
        string dynamicFormTemplateId,
        CancellationToken ct)
    {
        var form = await _ctx.DynamicFormTemplates
            .Find(x =>
                x.Id == dynamicFormTemplateId &&
                x.IsActive &&
                x.IsPublished &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Dynamic Form template không tồn tại hoặc chưa publish.");

        var excelId = NormalizeId(form.ExcelBlockDynamicExcelTemplateId)
            ?? ExtractExcelTemplateId(form.ExcelBlockJson);

        if (string.IsNullOrWhiteSpace(excelId))
            throw new InvalidOperationException("Dynamic Form template chưa có Excel block để chạy báo cáo hiện tại.");

        var excel = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == excelId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Excel block của Dynamic Form không tồn tại.");

        return new WorkAssignmentTemplateResolution(
            form.Id,
            form.Code,
            form.Name,
            excel.Id,
            excel.Code ?? string.Empty,
            excel.Name ?? string.Empty);
    }

    private async Task<WorkAssignmentTemplateResolution> ResolveLegacyDynamicExcelAsync(
        string dynamicExcelId,
        CancellationToken ct)
    {
        var excel = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == dynamicExcelId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Biểu mẫu/bảng động không tồn tại.");

        return new WorkAssignmentTemplateResolution(
            null,
            null,
            null,
            excel.Id,
            excel.Code ?? string.Empty,
            excel.Name ?? string.Empty);
    }

    private static string? ExtractExcelTemplateId(string? excelBlockJson)
    {
        if (string.IsNullOrWhiteSpace(excelBlockJson))
            return null;

        try
        {
            var snapshot = JsonSerializer.Deserialize<DynamicFormExcelBlockSnapshot>(
                excelBlockJson,
                JsonOptions);

            return NormalizeId(snapshot?.DynamicExcelTemplateId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
