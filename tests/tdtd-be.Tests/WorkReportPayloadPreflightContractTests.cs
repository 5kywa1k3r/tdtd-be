using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class WorkReportPayloadPreflightContractTests
{
    private const int PayloadTargetBytes = 4 * 1024 * 1024;
    private const string ReportId = "100000000000000000000091";
    private const string ActorUserId = "100000000000000000000092";

    public static void Run()
    {
        PurePreflightKeepsRevisionContractWithoutMutatingReport();
        OversizedLaterBlockFailsDuringPurePreflight();
        InvalidMongoIdentityFailsDuringPurePreflight();
        SavePreflightsAllDocumentsBeforeFirstMongoWrite();
        LegacyAdvancedSettingsAreNotInReportCommandJson();
    }

    private static void LegacyAdvancedSettingsAreNotInReportCommandJson()
    {
        const string json = """
            {
              "values1D": [],
              "dataOrigin": "AUTO_SUMMARY",
              "cumulativeContributionMode": "EXCLUDE",
              "cumulativeContributionPolicyJson": "{}",
              "summarySourceJson": "{}"
            }
            """;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var save = JsonSerializer.Deserialize<SaveWorkAssignmentReportDraftRequest>(json, options)!;
        var patch = JsonSerializer.Deserialize<SaveWorkAssignmentReportDraftPatchRequest>(json, options)!;
        var submit = JsonSerializer.Deserialize<SubmitWorkAssignmentReportRequest>(json, options)!;

        AssertEqual<string?>(null, save.DataOrigin, "save must ignore legacy data origin JSON");
        AssertEqual<string?>(null, patch.CumulativeContributionMode, "patch must ignore legacy cumulative JSON");
        AssertEqual<string?>(null, submit.SummarySourceJson, "submit must ignore legacy summary source JSON");

        var serialized = JsonSerializer.Serialize(new SaveWorkAssignmentReportDraftRequest
        {
            DataOrigin = "AUTO_SUMMARY",
            CumulativeContributionMode = "EXCLUDE",
            CumulativeContributionPolicyJson = "{}",
            SummarySourceJson = "{}"
        }, options);
        AssertNotContains(serialized, "dataOrigin", "save JSON contract must omit legacy Advanced settings");
        AssertNotContains(serialized, "cumulativeContribution", "save JSON contract must omit legacy cumulative settings");
        AssertNotContains(serialized, "summarySourceJson", "save JSON contract must omit legacy summary source");
    }

    private static void PurePreflightKeepsRevisionContractWithoutMutatingReport()
    {
        var report = new WorkAssignmentReport
        {
            Id = string.Empty,
            PayloadRevision = 7
        };
        var result = WorkReportPayloadService.PreflightReportPayload(
            report,
            "[1,null,3]",
            "{\"score\":3}",
            """
            {
              "schemaVersion": "1",
              "blocks": [
                { "blockId": "small", "tableMode": "FIXED_GRID", "w": 3, "h": 1, "values1D": [1, null, 3] }
              ]
            }
            """,
            null,
            ActorUserId,
            DateTime.UnixEpoch);

        AssertEqual(8, result.PayloadRevision, "preflight revision");
        AssertEqual(WorkReportPayloadStatus.Ready, result.PayloadStatus, "preflight status");
        AssertTrue(result.PayloadSizeBytes > 0, "preflight must report the complete payload size");
        AssertTrue(!string.IsNullOrWhiteSpace(result.PayloadHash), "preflight must compute the payload hash");
        AssertEqual(string.Empty, report.Id, "pure preflight must not assign a report id");
        AssertEqual(7, report.PayloadRevision, "pure preflight must not mutate the current revision");
    }

    private static void OversizedLaterBlockFailsDuringPurePreflight()
    {
        var table = new JsonObject
        {
            ["schemaVersion"] = "1",
            ["blocks"] = new JsonArray
            {
                new JsonObject
                {
                    ["blockId"] = "small",
                    ["tableMode"] = "FIXED_GRID",
                    ["values1D"] = new JsonArray(1)
                },
                new JsonObject
                {
                    ["blockId"] = "late_oversized",
                    ["tableMode"] = "FIXED_GRID",
                    ["blob"] = new string('x', PayloadTargetBytes)
                }
            }
        };

        var error = AssertThrows<AppException>(() => WorkReportPayloadService.PreflightReportPayload(
            Report(),
            "[]",
            null,
            table.ToJsonString(),
            null,
            ActorUserId,
            DateTime.UnixEpoch));

        AssertEqual(AppErrorCode.COMMON_VALIDATION_FAILED, error.Code, "oversized block error code");
        var rejectedId = error.Details?.GetType().GetProperty("id")?.GetValue(error.Details)?.ToString();
        AssertEqual($"{ReportId}:late_oversized", rejectedId, "oversized later block id");
    }

    private static void InvalidMongoIdentityFailsDuringPurePreflight()
    {
        var report = Report();
        report.Id = "not-an-object-id";

        AssertThrows<BsonSerializationException>(() => WorkReportPayloadService.PreflightReportPayload(
            report,
            "[]",
            null,
            null,
            null,
            ActorUserId,
            DateTime.UnixEpoch));
    }

    private static void SavePreflightsAllDocumentsBeforeFirstMongoWrite()
    {
        var source = ReadBackendSource("Services/WorkAssignmentReports/Payloads/WorkReportPayloadService.cs");
        var saveBody = Slice(
            source,
            "public async Task<WorkReportPayloadWriteResult> SaveReportPayloadAsync(",
            "public static WorkReportPayloadWriteResult PreflightReportPayload(");
        var prepareBody = Slice(
            source,
            "private static PreparedPayloadWrite PrepareReportPayload(",
            "private static void PreflightPreparedPayload(");
        var blockWriteBody = Slice(
            source,
            "private async Task SaveTableBlocksAsync(",
            "private async Task<IReadOnlyDictionary<string, string>> LoadExistingTableBlockIdsAsync(");

        AssertBefore(
            saveBody,
            "PreflightPreparedPayload(plan, actorUserId, now);",
            "_ctx.WorkReportPayloads",
            "pure payload preflight must run before the first Mongo access in Save");
        AssertBefore(
            saveBody,
            "PreflightMongoDocuments(payload, tableRows);",
            "await _ctx.WorkReportPayloads.ReplaceOneAsync(",
            "actual root and block documents must serialize before the first Mongo write");
        AssertContains(
            prepareBody,
            "foreach (var block in tableParts.Blocks)",
            "preflight must visit every table block");
        AssertContains(
            prepareBody,
            "GuardPayloadSize(",
            "preflight must apply payload budgets");
        AssertNotContains(
            blockWriteBody,
            "GuardPayloadSize(",
            "block writes must not defer a budget failure until after the root write");
    }

    private static WorkAssignmentReport Report()
        => new()
        {
            Id = ReportId,
            PayloadRevision = 3
        };

    private static string ReadBackendSource(string relativePath)
    {
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var directory = new DirectoryInfo(seed); directory is not null; directory = directory.Parent)
            {
                var direct = Path.Combine(directory.FullName, "tdtd-be.csproj");
                if (File.Exists(direct))
                    return File.ReadAllText(Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));

                var nestedRoot = Path.Combine(directory.FullName, "tdtd-be");
                if (File.Exists(Path.Combine(nestedRoot, "tdtd-be.csproj")))
                    return File.ReadAllText(Path.Combine(nestedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        throw new InvalidOperationException("Could not locate the tdtd-be source root.");
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        if (startIndex < 0 || endIndex < 0)
            throw new InvalidOperationException($"Source contract anchors were not found: {start} -> {end}");

        return source[startIndex..endIndex];
    }

    private static TException AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException error)
        {
            return error;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void AssertBefore(string source, string first, string second, string context)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        if (firstIndex < 0 || secondIndex < 0 || firstIndex >= secondIndex)
            throw new InvalidOperationException($"{context}: expected '{first}' before '{second}'.");
    }

    private static void AssertContains(string source, string expected, string context)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: expected '{expected}'.");
    }

    private static void AssertNotContains(string source, string forbidden, string context)
    {
        if (source.Contains(forbidden, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: unexpected '{forbidden}'.");
    }

    private static void AssertEqual<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected '{expected}', got '{actual}'.");
    }

    private static void AssertTrue(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }
}
