using System.Text.Json;
using Microsoft.AspNetCore.Http;
using tdtd_be.Common.Errors;
using tdtd_be.Services.WorkAssignmentReports;

internal static class WorkAssignmentReportRawReadAccessContractTests
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);

    public static void Run()
    {
        HiddenAndMissingReportsShareOneGenericForbiddenContract();
        SerializedForbiddenBodyContainsNoReportIdentifiers();
        BlockingMappingConflictBodyContainsOnlySafeSummary();
    }

    private static void HiddenAndMissingReportsShareOneGenericForbiddenContract()
    {
        var hidden = WorkAssignmentReportService
            .RawReportReadAccessForbidden();
        var missing = WorkAssignmentReportService
            .RawReportReadAccessForbidden();

        Require(
            hidden.Code ==
            AppErrorCode.WORK_ASSIGNMENT_REPORT_ACCESS_FORBIDDEN,
            "hidden raw report must use the stable access-forbidden code");
        Require(
            hidden.Descriptor.HttpStatus ==
            StatusCodes.Status403Forbidden,
            "hidden raw report must return HTTP 403");
        Require(
            hidden.Details is null &&
            missing.Details is null,
            "raw report non-enumeration failures must not expose details");

        var hiddenBody = AppErrorResponse.From(hidden, "trace-hidden");
        var missingBody = AppErrorResponse.From(missing, "trace-missing");
        Require(
            hiddenBody with { TraceId = string.Empty } ==
            missingBody with { TraceId = string.Empty },
            "hidden and missing raw reports must have the same body except traceId");
    }

    private static void SerializedForbiddenBodyContainsNoReportIdentifiers()
    {
        var response = AppErrorResponse.From(
            WorkAssignmentReportService.RawReportReadAccessForbidden(),
            "trace-safe");
        var body = JsonSerializer.Serialize(response, Json);

        Require(
            response.ErrorCode ==
            nameof(AppErrorCode.WORK_ASSIGNMENT_REPORT_ACCESS_FORBIDDEN),
            "serialized body must retain the stable error code");
        Require(
            response.Service == "REPORT" &&
            response.Details is null,
            "serialized body must remain generic");

        var forbiddenNames = new[]
        {
            "reportId",
            "workId",
            "workAssignmentId",
            "workReportPeriodId",
            "assigneeUserId",
            "periodKey",
            "periodInstanceKey",
            "\"status\"",
            "actorUserId"
        };
        foreach (var forbiddenName in forbiddenNames)
        {
            Require(
                !body.Contains(
                    forbiddenName,
                    StringComparison.OrdinalIgnoreCase),
                $"raw report forbidden body leaked {forbiddenName}");
        }
    }

    private static void BlockingMappingConflictBodyContainsOnlySafeSummary()
    {
        var error =
            WorkAssignmentReportService.DynamicFlowMappingBlockingConflict();
        Require(
            error.Code == AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
            "blocking mapping conflict must retain the stable report error code");
        Require(
            error.Descriptor.HttpStatus ==
            StatusCodes.Status400BadRequest,
            "blocking mapping conflict must remain HTTP 400");

        var response = AppErrorResponse.From(error, "trace-safe-conflict");
        var body = JsonSerializer.Serialize(response, Json);
        using var details = JsonDocument.Parse(
            JsonSerializer.Serialize(response.Details, Json));
        var properties = details.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Require(
            properties.SequenceEqual(
                new[] { "hasBlockingConflicts", "reason" },
                StringComparer.Ordinal),
            "blocking conflict details must contain only the safe summary fields");
        Require(
            details.RootElement
                .GetProperty("reason")
                .GetString() ==
            "DYNAMIC_FLOW_MAPPING_CONFLICT",
            "blocking mapping conflict reason");
        Require(
            details.RootElement
                .GetProperty("hasBlockingConflicts")
                .GetBoolean(),
            "blocking mapping conflict marker");

        foreach (var forbiddenName in new[]
                 {
                     "\"changes\"",
                     "\"sources\"",
                     "valueJson",
                     "previousValueJson",
                     "nextValueJson",
                     "sourceReportId",
                     "sourceAssignmentId",
                     "sourcePayloadHash",
                     "reportId",
                     "workAssignmentId",
                     "actorUserId"
                 })
        {
            Require(
                !body.Contains(
                    forbiddenName,
                    StringComparison.OrdinalIgnoreCase),
                $"blocking mapping conflict leaked {forbiddenName}");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
