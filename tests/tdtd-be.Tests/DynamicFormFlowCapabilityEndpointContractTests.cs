using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Capabilities;
using tdtd_be.Controllers;
using tdtd_be.DTOs.Capabilities;

internal static class DynamicFormFlowCapabilityEndpointContractTests
{
    public static void Run()
    {
        var controller = CreateController();
        var response = controller.Get();
        var ok = response.Result as OkObjectResult
            ?? throw new InvalidOperationException("Capability endpoint must return 200 for a fresh request.");
        var catalog = ok.Value as DynamicFormFlowCapabilityCatalogDto
            ?? throw new InvalidOperationException("Capability endpoint must return a typed catalog response.");

        AssertEqual(DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion, catalog.CatalogVersion, "catalog version");
        AssertEqual(DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256, catalog.CatalogSha256, "catalog hash");
        AssertEqual(12, catalog.Domains.DynamicFlowArchetypes.Count, "flow archetype count");
        AssertEqual(10, catalog.Domains.DynamicFlowMappingCapabilities.Count, "mapping capability count");
        var p8ConfigurationCapabilities = catalog.Domains.StatisticsConfigurationCapabilities
            ?? throw new InvalidOperationException("Active v1.6/v1.7 endpoint must expose P8 configuration capabilities.");
        AssertEqual(11, p8ConfigurationCapabilities.Count, "P8 configuration capability count");
        AssertEqual(
            "FIELD_TYPED|FIXED_GRID|APPEND_ROWS|MATRIX_SPARSE|SOURCE_REPORT_GRAIN|" +
            "GROUP_GRAIN|CUSTOM_JOIN_KEY|APPEND_COLUMNS_TARGET|SCALAR_TO_ROW|ROW_TO_REPORT",
            string.Join("|", catalog.Domains.DynamicFlowMappingCapabilities.Select(item => item.Id)),
            "mapping capability IDs");
        AssertEqual(
            "SUPPORTED|SUPPORTED|SUPPORTED|SUPPORTED|SUPPORTED|" +
            "INTENTIONAL_BLOCK|INTENTIONAL_BLOCK|INTENTIONAL_BLOCK|INTENTIONAL_BLOCK|INTENTIONAL_BLOCK",
            string.Join("|", catalog.Domains.DynamicFlowMappingCapabilities.Select(item => item.Status)),
            "mapping capability statuses");
        AssertEqual(
            "P7|P7|P7|P7|P7|||||",
            string.Join("|", catalog.Domains.DynamicFlowMappingCapabilities.Select(item => item.TargetPhase)),
            "mapping capability phases");
        AssertEqual(
            "LABEL_TAXONOMY_CONFIG|FIELD_METADATA_CONFIG|TABLE_METADATA_CONFIG|BASIC_SUMMARY_CONFIG|" +
            "FLOW_SCOPE_CONFIG|ADVANCED_SUMMARY_CONFIG|DIFF_CONFIG|FLOW_CONTRIBUTION_CONFIG|" +
            "FLOW_STATISTIC_PROFILE_BARRIER|CONFIG_OPERATIONS_READINESS|CONFIG_BUNDLE_READBACK",
            string.Join("|", p8ConfigurationCapabilities.Select(item => item.Id)),
            "P8 configuration capability IDs");
        AssertEqual(
            "INTENTIONAL_BLOCK",
            catalog.Domains.StatisticsCapabilities.Single(item => item.Id == "FLOW_STATISTIC_PROFILE").Status,
            "P9 statistic profile status");

        var reconciliation = catalog.Domains.StatisticsReconciliationCapabilities;
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var wireCatalog = JsonDocument.Parse(JsonSerializer.Serialize(catalog, jsonOptions));
        var wireDomains = wireCatalog.RootElement.GetProperty("domains");
        switch (DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion)
        {
            case "1.6":
                if (reconciliation is not null)
                    throw new InvalidOperationException("Active v1.6 endpoint must omit P10 reconciliation capabilities.");
                if (wireDomains.TryGetProperty("statisticsReconciliationCapabilities", out _))
                    throw new InvalidOperationException("Active v1.6 JSON must omit statisticsReconciliationCapabilities.");
                break;
            case "1.7":
                var p10Capabilities = reconciliation
                    ?? throw new InvalidOperationException("Active v1.7 endpoint must expose P10 reconciliation capabilities.");
                AssertEqual(4, p10Capabilities.Count, "P10 reconciliation capability count");
                var wireReconciliation = wireDomains.GetProperty("statisticsReconciliationCapabilities");
                AssertEqual(4, wireReconciliation.GetArrayLength(), "P10 wire reconciliation capability count");
                AssertEqual(
                    "SOURCE_TO_RESULT_RECONCILIATION|EXPECTED_ACTUAL_DELTA|INDEPENDENT_REVIEW_SIGNOFF|RECONCILIATION_EVIDENCE_EXPORT",
                    string.Join("|", wireReconciliation.EnumerateArray().Select(item => item.GetProperty("id").GetString())),
                    "P10 wire reconciliation capability IDs");
                AssertEqual(
                    "SOURCE_TO_RESULT_RECONCILIATION~Source-to-result statistics reconciliation~SUPPORTED~P10~P10-RECONCILE~STAT_RECONCILIATION~<null>|" +
                    "EXPECTED_ACTUAL_DELTA~Expected-versus-actual typed delta~SUPPORTED~P10~P10-DELTA~STAT_RECONCILIATION~<null>|" +
                    "INDEPENDENT_REVIEW_SIGNOFF~Independent reconciliation review sign-off~SUPPORTED~P10~P10-REVIEW~STAT_RECONCILIATION_REVIEW~<null>|" +
                    "RECONCILIATION_EVIDENCE_EXPORT~Deterministic reconciliation evidence export~SUPPORTED~P10~P10-EVIDENCE~STAT_RECONCILIATION_EVIDENCE~<null>",
                    string.Join("|", p10Capabilities.Select(item =>
                        $"{item.Id}~{item.Name}~{item.Status}~{item.TargetPhase}~{item.TestPrefix}~{item.UiSurface}~{item.Notes ?? "<null>"}")),
                    "P10 reconciliation capability metadata");
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown catalog version '{DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion}'.");
        }

        AssertEqual(
            $"\"{DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256}\"",
            controller.Response.Headers.ETag.ToString(),
            "response ETag");

        var conditionalController = CreateController();
        conditionalController.Request.Headers.IfNoneMatch = $"W/\"{catalog.CatalogSha256}\"";
        var conditionalResponse = conditionalController.Get();
        var notModified = conditionalResponse.Result as StatusCodeResult
            ?? throw new InvalidOperationException("Matching If-None-Match must return a status result.");
        AssertEqual(StatusCodes.Status304NotModified, notModified.StatusCode, "conditional response status");
    }

    private static DynamicFormFlowCapabilitiesController CreateController()
        => new()
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {label} to be '{expected}', got '{actual}'.");
        }
    }
}
