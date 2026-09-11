using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

namespace tdtd_be.Services.WorkAssignmentReports.Runtime;

public sealed record WorkReportLifecycleCommand(
    int ExpectedLifecycleRevision,
    int ExpectedPayloadRevision,
    string CommandId,
    string Operation,
    string CommandHash,
    int PayloadRevisionDelta);

public enum WorkReportLifecycleCommandResolution
{
    NewCommand,
    CompletedReplay
}

/// <summary>
/// Shared lifecycle CAS contract for reporter and reviewer entry points. It deliberately keeps
/// payload revision and lifecycle revision separate: content commits advance the former, while
/// status/active-state commits advance the latter.
/// </summary>
public static class WorkReportLifecycleCommandContract
{
    private static readonly Regex CommandIdRegex = new(
        "^[A-Za-z0-9][A-Za-z0-9._:-]{7,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static readonly HashSet<string> ProtocolPropertyNames = new(StringComparer.Ordinal)
    {
        "commandId",
        "expectedLifecycleRevision",
        "expectedPayloadRevision"
    };

    public static string ComputeHash(string operation, object request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var serialized = JsonSerializer.SerializeToElement(request, request.GetType(), JsonOptions);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonicalJson(writer, serialized, isRequestRoot: true);
            writer.Flush();
        }

        var json = Encoding.UTF8.GetString(buffer.ToArray());
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes($"{operation}\n{json}")))
            .ToLowerInvariant();
    }

    private static void WriteCanonicalJson(
        Utf8JsonWriter writer,
        JsonElement value,
        bool isRequestRoot = false)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value
                             .EnumerateObject()
                             .Where(x => !isRequestRoot || !ProtocolPropertyNames.Contains(x.Name))
                             .OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonicalJson(writer, item);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                var text = value.GetString()?.Trim() ?? string.Empty;
                writer.WriteStringValue(TryCanonicalizeEmbeddedJson(text, out var canonicalJson)
                    ? canonicalJson
                    : text);
                break;

            case JsonValueKind.Number:
                if (value.TryGetInt64(out var signedInteger))
                    writer.WriteNumberValue(signedInteger);
                else if (value.TryGetUInt64(out var unsignedInteger))
                    writer.WriteNumberValue(unsignedInteger);
                else if (value.TryGetDecimal(out var decimalValue))
                    writer.WriteNumberValue(decimalValue);
                else
                    writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;

            default:
                throw new JsonException($"Unsupported lifecycle request value kind: {value.ValueKind}.");
        }
    }

    private static bool TryCanonicalizeEmbeddedJson(string value, out string canonicalJson)
    {
        canonicalJson = value;
        if (value.Length < 2 ||
            (value[0] != '{' && value[0] != '['))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                return false;

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
            {
                WriteCanonicalJson(writer, document.RootElement);
                writer.Flush();
            }
            canonicalJson = Encoding.UTF8.GetString(buffer.ToArray());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static (WorkReportLifecycleCommand Command, WorkReportLifecycleCommandResolution Resolution) Resolve(
        WorkAssignmentReport report,
        int? expectedLifecycleRevision,
        int? expectedPayloadRevision,
        string? commandId,
        string operation,
        string commandHash,
        int payloadRevisionDelta = 0)
    {
        if (!expectedLifecycleRevision.HasValue || expectedLifecycleRevision.Value < 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_REQUIRED,
                new
                {
                    reportId = report.Id,
                    expectedLifecycleRevision,
                    currentLifecycleRevision = report.LifecycleRevision
                });
        }

        if (!expectedPayloadRevision.HasValue || expectedPayloadRevision.Value < 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_PAYLOAD_REVISION_REQUIRED,
                new
                {
                    reportId = report.Id,
                    expectedPayloadRevision,
                    currentPayloadRevision = report.PayloadRevision
                });
        }

        var normalizedCommandId = commandId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCommandId) || !CommandIdRegex.IsMatch(normalizedCommandId))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_COMMAND_ID_REQUIRED,
                new
                {
                    reportId = report.Id,
                    commandId,
                    minLength = 8,
                    maxLength = 128
                });
        }

        var command = new WorkReportLifecycleCommand(
            expectedLifecycleRevision.Value,
            expectedPayloadRevision.Value,
            normalizedCommandId,
            operation,
            commandHash,
            payloadRevisionDelta);

        if (string.Equals(report.LastLifecycleCommandId, command.CommandId, StringComparison.Ordinal))
        {
            if (!IsCompletedReplay(report, command))
                throw ReplayMismatch(report, command);

            return (command, WorkReportLifecycleCommandResolution.CompletedReplay);
        }

        if (command.ExpectedLifecycleRevision != report.LifecycleRevision ||
            command.ExpectedPayloadRevision != report.PayloadRevision)
        {
            throw Conflict(report, command);
        }

        return (command, WorkReportLifecycleCommandResolution.NewCommand);
    }

    public static bool IsCompletedReplay(
        WorkAssignmentReport report,
        WorkReportLifecycleCommand command)
        => string.Equals(report.LastLifecycleCommandId, command.CommandId, StringComparison.Ordinal) &&
           string.Equals(report.LastLifecycleCommandOperation, command.Operation, StringComparison.Ordinal) &&
           string.Equals(report.LastLifecycleCommandHash, command.CommandHash, StringComparison.Ordinal) &&
            report.LastLifecycleCommandRevision == report.LifecycleRevision &&
            report.LastLifecycleCommandPayloadRevision == report.PayloadRevision &&
            report.LastLifecycleCommandStatus == report.Status &&
            report.LastLifecycleCommandIsActive == report.IsActive &&
            command.ExpectedLifecycleRevision == report.LifecycleRevision - 1 &&
           command.ExpectedPayloadRevision + command.PayloadRevisionDelta == report.PayloadRevision;

    public static FilterDefinition<WorkAssignmentReport> BuildCommitFilter(
        WorkAssignmentReport report,
        WorkReportLifecycleCommand command,
        WorkAssignmentReportStatus expectedStatus,
        bool expectedIsActive = true,
        string? expectedPayloadMutationCommandId = null)
    {
        var fb = Builders<WorkAssignmentReport>.Filter;
        var payloadRevisionFilter = command.ExpectedPayloadRevision == 0
            ? fb.Eq(x => x.PayloadRevision, 0) | fb.Exists(x => x.PayloadRevision, false)
            : fb.Eq(x => x.PayloadRevision, command.ExpectedPayloadRevision);
        var lifecycleRevisionFilter = command.ExpectedLifecycleRevision == 0
            ? fb.Eq(x => x.LifecycleRevision, 0) | fb.Exists(x => x.LifecycleRevision, false)
            : fb.Eq(x => x.LifecycleRevision, command.ExpectedLifecycleRevision);
        var payloadMutationFilter = string.IsNullOrWhiteSpace(expectedPayloadMutationCommandId)
            ? fb.Eq(x => x.PayloadMutationCommandId, null)
            : fb.Eq(x => x.PayloadMutationCommandId, expectedPayloadMutationCommandId);

        return fb.Eq(x => x.Id, report.Id) &
               fb.Eq(x => x.IsDeleted, false) &
               fb.Eq(x => x.AssigneeUserId, report.AssigneeUserId) &
               fb.Eq(x => x.IsActive, expectedIsActive) &
               fb.Eq(x => x.Status, expectedStatus) &
               payloadRevisionFilter &
               lifecycleRevisionFilter &
               payloadMutationFilter;
    }

    public static UpdateDefinition<WorkAssignmentReport> ApplyCompletion(
        UpdateDefinition<WorkAssignmentReport> update,
        WorkReportLifecycleCommand command,
        WorkAssignmentReportStatus resultStatus,
        bool resultIsActive)
        => update
            .Set(x => x.LifecycleRevision, command.ExpectedLifecycleRevision + 1)
            .Set(x => x.LastLifecycleCommandId, command.CommandId)
            .Set(x => x.LastLifecycleCommandHash, command.CommandHash)
            .Set(x => x.LastLifecycleCommandOperation, command.Operation)
            .Set(x => x.LastLifecycleCommandRevision, command.ExpectedLifecycleRevision + 1)
            .Set(x => x.LastLifecycleCommandPayloadRevision, command.ExpectedPayloadRevision + command.PayloadRevisionDelta)
            .Set(x => x.LastLifecycleCommandStatus, resultStatus)
            .Set(x => x.LastLifecycleCommandIsActive, resultIsActive);

    public static void ApplyCompletionInMemory(
        WorkAssignmentReport report,
        WorkReportLifecycleCommand command,
        WorkAssignmentReportStatus resultStatus,
        bool resultIsActive)
    {
        report.LifecycleRevision = command.ExpectedLifecycleRevision + 1;
        report.LastLifecycleCommandId = command.CommandId;
        report.LastLifecycleCommandHash = command.CommandHash;
        report.LastLifecycleCommandOperation = command.Operation;
        report.LastLifecycleCommandRevision = report.LifecycleRevision;
        report.LastLifecycleCommandPayloadRevision = command.ExpectedPayloadRevision + command.PayloadRevisionDelta;
        report.LastLifecycleCommandStatus = resultStatus;
        report.LastLifecycleCommandIsActive = resultIsActive;
    }

    public static AppException Conflict(
        WorkAssignmentReport report,
        WorkReportLifecycleCommand command)
        => AppExceptionFactory.Create(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT,
            new
            {
                reportId = report.Id,
                commandId = command.CommandId,
                operation = command.Operation,
                expectedLifecycleRevision = command.ExpectedLifecycleRevision,
                currentLifecycleRevision = report.LifecycleRevision,
                expectedPayloadRevision = command.ExpectedPayloadRevision,
                currentPayloadRevision = report.PayloadRevision,
                report.Status,
                report.IsActive,
                report.UpdatedAtUtc,
                report.UpdatedByUserId
            });

    public static AppException ReplayMismatch(
        WorkAssignmentReport report,
        WorkReportLifecycleCommand command)
        => AppExceptionFactory.Create(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_COMMAND_REPLAY_MISMATCH,
            new
            {
                reportId = report.Id,
                commandId = command.CommandId,
                operation = command.Operation,
                expectedLifecycleRevision = command.ExpectedLifecycleRevision,
                currentLifecycleRevision = report.LifecycleRevision,
                expectedPayloadRevision = command.ExpectedPayloadRevision,
                currentPayloadRevision = report.PayloadRevision
            });
}
