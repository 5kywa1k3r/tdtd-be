using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.Services.WorkAssignments.Lookups;

public sealed class WorkAssignmentTemplateResolver : IWorkAssignmentTemplateResolver
{
    private readonly MongoDbContext _ctx;

    public WorkAssignmentTemplateResolver(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<WorkAssignmentTemplateResolution> ResolveAsync(
        string? dynamicFormTemplateId,
        string actorUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            throw AppExceptionFactory.Unauthorized();

        if (string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.DYNAMIC_FORM_TEMPLATE_REQUIRED);

        var actor = await _ctx.Users
            .Find(x => x.Id == actorUserId.Trim() && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.Unauthorized();

        return await ResolveDynamicFormAsync(dynamicFormTemplateId.Trim(), actor, ct);
    }

    private async Task<WorkAssignmentTemplateResolution> ResolveDynamicFormAsync(
        string dynamicFormTemplateId,
        AppUser actor,
        CancellationToken ct)
    {
        var form = await _ctx.DynamicFormTemplates
            .Find(DynamicFormBindingAccessPolicy.BuildMayBindFilter(
                actor,
                new[] { dynamicFormTemplateId },
                requirePublished: true))
            .FirstOrDefaultAsync(ct)
            ?? throw DynamicFormBindingAccessPolicy.Forbidden();

        // Integrity is evaluated only after the Mongo ACL projection has
        // established that this actor may bind the exact Form.
        DynamicFormBindingAccessPolicy.EnsureMayBind(actor, new[] { form });

        var excelId = NormalizeId(form.ExcelBlockDynamicExcelTemplateId)
            ?? ExtractExcelTemplateId(form.ExcelBlockJson)
            ?? ExtractExcelTemplateIdFromBlocks(form.BlocksJson);

        if (string.IsNullOrWhiteSpace(excelId))
        {
            return new WorkAssignmentTemplateResolution(
                form.Id,
                form.Code,
                form.Name,
                null,
                string.Empty,
                string.Empty);
        }

        var excel = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == excelId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_FORM_EXCEL_BLOCK_NOT_FOUND,
                new { dynamicFormTemplateId, dynamicExcelTemplateId = excelId });

        return new WorkAssignmentTemplateResolution(
            form.Id,
            form.Code,
            form.Name,
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
            using var document = JsonDocument.Parse(excelBlockJson);
            return ExtractExcelTemplateId(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractExcelTemplateIdFromBlocks(string? blocksJson)
    {
        if (string.IsNullOrWhiteSpace(blocksJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(blocksJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var id = ExtractExcelTemplateId(item);
                if (!string.IsNullOrWhiteSpace(id))
                    return id;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? ExtractExcelTemplateId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (root.TryGetProperty("dynamicExcelTemplateId", out var camel)
            && camel.ValueKind == JsonValueKind.String)
        {
            return NormalizeId(camel.GetString());
        }

        if (root.TryGetProperty("DynamicExcelTemplateId", out var pascal)
            && pascal.ValueKind == JsonValueKind.String)
        {
            return NormalizeId(pascal.GetString());
        }

        return null;
    }

    private static string? NormalizeId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
