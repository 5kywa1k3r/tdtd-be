using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicExcel;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Services;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/dynamic-forms")]
[Authorize]
public sealed class DynamicFormController : ControllerBase
{
    private readonly IDynamicFormService _svc;

    public DynamicFormController(IDynamicFormService svc)
    {
        _svc = svc;
    }

    [HttpPost("search")]
    public Task<PagedResult<DynamicFormRow>> Search([FromBody] DynamicFormSearchReq req, CancellationToken ct)
        => _svc.SearchAsync(req, ct);

    [HttpPost("owned-section-sources/query")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<PagedResult<DynamicFormSectionSourceRow>> SectionSources(
        [FromBody] DynamicFormSectionSourceQuery req, CancellationToken ct)
        => _svc.SearchSectionSourcesAsync(req, ct);

    [HttpGet("owned-section-sources/{id}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<DynamicFormDetail> SectionSource([FromRoute] string id, CancellationToken ct)
        => _svc.GetSectionSourceAsync(id, ct);

    // Protocol support only; mutation permissions and revision guards remain per Form.
    [HttpGet("definition-capabilities")]
    public DynamicFormNativeDefinitionCapabilities DefinitionCapabilities()
        => new("dynamic-form-native-table-definition", DynamicFormNativeTableDefinition.Version,
            SupportsCreateDraft: true, SupportsUpdateDraft: true, SupportsPublish: true, SupportsStatistics: true,
            NativeStatisticPlanVersion: 2, NativeListDefinitionVersion: DynamicFormNativeTableDefinition.ListVersion,
            SupportsLists: true);

    // Versioned routes cannot be accepted by an old server that silently ignores schema.tables.
    [HttpPost("native-definitions/v1")]
    public Task<DynamicFormDetail> CreateNativeDefinition([FromBody] CreateDynamicFormReq req, CancellationToken ct)
    {
        RequireNativeDefinition(req.Schema);
        return _svc.CreateAsync(req, ct);
    }

    [HttpPut("native-definitions/v1/{id}")]
    public Task<DynamicFormDetail> UpdateNativeDefinition([FromRoute] string id, [FromBody] UpdateDynamicFormReq req, CancellationToken ct)
    {
        RequireNativeDefinition(req.Schema);
        return _svc.UpdateAsync(id, req, ct);
    }

    [HttpPost("native-definitions/v2")]
    public Task<DynamicFormDetail> CreateNativeListDefinition([FromBody] CreateDynamicFormReq req, CancellationToken ct)
    {
        RequireNativeDefinition(req.Schema, DynamicFormNativeTableDefinition.ListVersion);
        return _svc.CreateAsync(req, ct);
    }

    [HttpPut("native-definitions/v2/{id}")]
    public Task<DynamicFormDetail> UpdateNativeListDefinition([FromRoute] string id, [FromBody] UpdateDynamicFormReq req, CancellationToken ct)
    {
        RequireNativeDefinition(req.Schema, DynamicFormNativeTableDefinition.ListVersion);
        return _svc.UpdateAsync(id, req, ct);
    }

    private static void RequireNativeDefinition(DynamicFormSchemaDto? schema)
        => RequireNativeDefinition(schema, DynamicFormNativeTableDefinition.Version);

    private static void RequireNativeDefinition(DynamicFormSchemaDto? schema, int version)
    {
        if (schema is null || schema.NativeTablesVersion != version || schema.Tables is null)
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED,
                new { reason = "NATIVE_TABLE_VERSION_OR_COLLECTION_INVALID", path = "schema.tables" });
    }

    [HttpGet("next-code")]
    public Task<NextCodeResp> NextCode([FromQuery] int? year, CancellationToken ct)
        => _svc.GetNextCodeAsync(year, ct);

    [HttpGet("code-availability")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<DynamicFormCodeAvailability> CodeAvailability([FromQuery] string? code, CancellationToken ct)
        => _svc.GetCodeAvailabilityAsync(code, ct);

    [HttpGet("{id}")]
    public Task<DynamicFormDetail> GetById([FromRoute] string id, CancellationToken ct)
        => _svc.GetByIdAsync(id, ct);

    [HttpGet("{id}/versions")]
    public Task<DynamicFormVersionHistoryResp> GetVersionHistory(
        [FromRoute] string id,
        CancellationToken ct)
        => _svc.GetVersionHistoryAsync(id, ct);

    // Owner/admin diagnostic only. Never invokes schema/P8 normalization or migration.
    [HttpGet("{id}/storage-diagnostic/v1")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<DynamicFormStorageDiagnostic> GetStorageDiagnostic([FromRoute] string id, CancellationToken ct)
        => _svc.GetStorageDiagnosticAsync(id, ct);

    [HttpGet("{id}/reference-assessment/v1")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<DynamicFormReferenceAssessment> GetReferenceAssessment([FromRoute] string id,
        [FromQuery] string expectedStorageSha256, CancellationToken ct)
        => _svc.GetReferenceAssessmentAsync(id, expectedStorageSha256, ct);

    [HttpGet("{id}/dependency-assessment/v1")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<DynamicFormDependencyAssessment> GetDependencyAssessment([FromRoute] string id,
        [FromQuery] string expectedStorageSha256, CancellationToken ct)
        => _svc.GetDependencyAssessmentAsync(id, expectedStorageSha256, ct);

    [HttpGet("{id}/artifact-assessment/v1")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<DynamicFormArtifactAssessment> GetArtifactAssessment([FromRoute] string id,
        [FromQuery] string expectedStorageSha256, CancellationToken ct)
        => _svc.GetArtifactAssessmentAsync(id, expectedStorageSha256, ct);

    [HttpGet("{id}/snapshot-assessment/v1")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<DynamicFormSnapshotAssessment> GetSnapshotAssessment([FromRoute] string id,
        [FromQuery] string expectedStorageSha256, [FromQuery] string kind, [FromQuery] string snapshotId,
        [FromQuery] string? refreshId, CancellationToken ct)
        => _svc.GetSnapshotAssessmentAsync(id, expectedStorageSha256, kind, snapshotId, refreshId, ct);

    [HttpPost]
    public Task<DynamicFormDetail> Create([FromBody] CreateDynamicFormReq req, CancellationToken ct)
        => _svc.CreateAsync(req, ct);

    [HttpPost("{id}/versions")]
    public Task<DynamicFormDetail> CreateNextVersion(
        [FromRoute] string id,
        [FromBody] CreateDynamicFormVersionReq req,
        CancellationToken ct)
        => _svc.CreateNextVersionAsync(id, req, ct);

    [HttpPut("{id}")]
    public Task<DynamicFormDetail> Update(
        [FromRoute] string id,
        [FromBody] UpdateDynamicFormReq req,
        CancellationToken ct)
        => _svc.UpdateAsync(id, req, ct);

    [HttpGet("{id}/statistics")]
    public Task<DynamicFormStatisticConfigResult> GetStatistics(
        [FromRoute] string id,
        CancellationToken ct)
        => _svc.GetStatisticsAsync(id, ct);

    [HttpPatch("{id}/statistics")]
    public Task<DynamicFormStatisticConfigResult> UpdateStatisticConfig(
        [FromRoute] string id,
        [FromBody] JsonElement body,
        CancellationToken ct)
        // Retain the read endpoint for existing Form revisions, but retire all
        // P8 authoring (fields, native Tables and legacy Excel tables).
        => throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED,
            new { reason = "FORM_LEGACY_STATISTIC_AUTHORING_DISABLED" });

    [HttpPost("{id}/publish")]
    public Task<DynamicFormDetail> Publish(
        [FromRoute] string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PublishDynamicFormReq? req,
        CancellationToken ct)
        => _svc.PublishAsync(id, req, ct);

    [HttpPost("{id}/clone")]
    public Task<DynamicFormDetail> Clone(
        [FromRoute] string id,
        [FromBody] CloneDynamicFormReq req,
        CancellationToken ct)
        => _svc.CloneAsync(id, req, ct);

    // Đợt nghiệm thu: khóa cả các điểm nhập từ Bảng biểu động sang Biểu mẫu động.
    [NonAction]
    [HttpPost("wrap-dynamic-excel")]
    public Task<DynamicFormDetail> WrapDynamicExcel(
        [FromBody] WrapDynamicExcelAsFormReq req,
        CancellationToken ct)
        => _svc.WrapDynamicExcelAsync(req, ct);

    [NonAction]
    [HttpPost("{id}/blocks/import-dynamic-excel")]
    public Task<DynamicFormDetail> ImportDynamicExcelBlock(
        [FromRoute] string id,
        [FromBody] ImportDynamicExcelBlockReq req,
        CancellationToken ct)
        => _svc.ImportDynamicExcelBlockAsync(id, req, ct);

    [HttpDelete("{id}")]
    public Task Delete(
        [FromRoute] string id,
        [FromQuery] int? expectedRevision,
        CancellationToken ct)
        => _svc.DeleteAsync(id, expectedRevision, ct);
}
