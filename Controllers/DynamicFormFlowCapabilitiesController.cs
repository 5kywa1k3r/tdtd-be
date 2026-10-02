using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Capabilities;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Capabilities;

namespace tdtd_be.Controllers;

// Catalog khả năng Flow cũng tạm khóa cùng chức năng Flow ở bản deploy này.
[NonController]
[ApiController]
[Authorize]
[Route("api/capabilities/dynamic-form-flow")]
public sealed class DynamicFormFlowCapabilitiesController : ControllerBase
{
    private static readonly string CatalogEtag = $"\"{DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256}\"";

    private static readonly DynamicFormFlowCapabilityCatalogDto Catalog = new(
        DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
        DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
        DynamicFormFlowCapabilityCatalogMetadata.SchemaSha256,
        DynamicFormFlowCapabilityCatalogMetadata.ApprovedAt,
        new DynamicFormFlowCapabilityTerminologyDto(
            DynamicFormFlowCapabilityCatalogMetadata.FlowScopeName,
            DynamicFormFlowCapabilityCatalogMetadata.FullBpmnClaimAllowed),
        DynamicFormFlowCapabilityCatalogMetadata.Defaults,
        new DynamicFormFlowCapabilityUiGateDto(
            DynamicFormFlowCapabilityCatalogMetadata.UiGateContinuous,
            DynamicFormFlowCapabilityCatalogMetadata.RequiredUiStates,
            DynamicFormFlowCapabilityCatalogMetadata.RequiredActors,
            DynamicFormFlowCapabilityCatalogMetadata.RequiredEvidence),
        new DynamicFormFlowCapabilityDomainsDto(
            DynamicFormFlowCapabilityCatalogMetadata.DynamicFormFieldTypes,
            DynamicFormFlowCapabilityCatalogMetadata.DynamicFormValueSources,
            DynamicFormFlowCapabilityCatalogMetadata.DynamicFormTableModes,
            DynamicFormFlowCapabilityCatalogMetadata.DynamicFlowArchetypes,
            DynamicFormFlowCapabilityCatalogMetadata.StatisticsCapabilities,
            DynamicFormFlowCapabilityCatalogMetadata.DynamicFlowMappingCapabilities,
            DynamicFormFlowCapabilityCatalogMetadata.StatisticsConfigurationCapabilities.Count == 0
                ? null
                : DynamicFormFlowCapabilityCatalogMetadata.StatisticsConfigurationCapabilities,
            DynamicFormFlowCapabilityCatalogMetadata.StatisticsReconciliationCapabilities.Count == 0
                ? null
                : DynamicFormFlowCapabilityCatalogMetadata.StatisticsReconciliationCapabilities));

    [HttpGet]
    [ProducesResponseType(typeof(DynamicFormFlowCapabilityCatalogDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    public ActionResult<DynamicFormFlowCapabilityCatalogDto> Get()
    {
        Response.Headers.ETag = CatalogEtag;
        Response.Headers.CacheControl = "private, max-age=0, must-revalidate";

        if (MatchesCatalogEtag(Request.Headers.IfNoneMatch.ToString()))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(Catalog);
    }

    private static bool MatchesCatalogEtag(string ifNoneMatch)
    {
        foreach (var rawCandidate in ifNoneMatch.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = rawCandidate.Trim();
            if (candidate == "*")
            {
                return true;
            }

            if (candidate.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate[2..].Trim();
            }

            if (string.Equals(candidate, CatalogEtag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
