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

    public async Task<WorkAssignmentTemplateResolution> ResolveForChildAsync(
        string? dynamicFormTemplateId,
        string actorUserId,
        string workId,
        string parentAssignmentId,
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
        var parent = await _ctx.WorkAssignments
            .Find(x => x.Id == parentAssignmentId && x.WorkId == workId && !x.IsDeleted && x.IsActive)
            .FirstOrDefaultAsync(ct);
        if (parent is null || parent.InvalidatedByFlowEventId is not null ||
            !string.IsNullOrWhiteSpace(parent.FlowInstanceId) ||
            (parent.CreatedByUserId != actor.Id && !parent.Assignees.Any(x => x.UserId == actor.Id)))
            throw DynamicFormBindingAccessPolicy.Forbidden();

        var formId = dynamicFormTemplateId.Trim();
        // Other Forms retain the normal owner/admin rule. Runtime read access alone
        // never authorizes binding a received Form in an unrelated branch or work.
        if (!string.Equals(parent.DynamicFormTemplateId, formId, StringComparison.Ordinal))
            return await ResolveDynamicFormAsync(formId, actor, ct);

        var form = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == formId && !x.IsDeleted && x.IsActive && x.IsPublished)
            .FirstOrDefaultAsync(ct)
            ?? throw DynamicFormBindingAccessPolicy.Forbidden();
        var familyId = string.IsNullOrWhiteSpace(form.FamilyId) ? form.Id : form.FamilyId;
        if (parent.DynamicFormFamilyId != familyId ||
            parent.DynamicFormVersionNo != Math.Max(1, form.VersionNo) ||
            string.IsNullOrWhiteSpace(parent.DynamicFormSchemaHash) ||
            !string.Equals(parent.DynamicFormSchemaHash, form.PublishedSchemaHash, StringComparison.Ordinal))
            throw DynamicFormBindingAccessPolicy.Forbidden();

        DynamicFormBindingAccessPolicy.EnsurePublishedIntegrity(form);
        return await BuildResolutionAsync(form, ct);
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

        return await BuildResolutionAsync(form, ct);
    }

    private async Task<WorkAssignmentTemplateResolution> BuildResolutionAsync(
        DynamicFormTemplate form,
        CancellationToken ct)
    {

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
                new { dynamicFormTemplateId = form.Id, dynamicExcelTemplateId = excelId });

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
