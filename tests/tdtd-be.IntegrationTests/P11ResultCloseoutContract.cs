using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

// Pure closeout decisions. No browser, database, process or filesystem operations.
internal static class P11ResultCloseoutContract
{
    internal static readonly string[] ExpectedCases =
        Enumerable.Range(1, 6).Select(n => $"P11-STAT-{n:00}")
            .Concat(Enumerable.Range(1, 6).Select(n => $"P11-RECON-{n:00}"))
            .Concat(Enumerable.Range(1, 4).Select(n => $"P11-EVID-{n:00}")).ToArray();

    internal static readonly string[] ObjectIdKeys =
    {
        "formVersionId", "flowFamilyId", "flowVersionId", "workId", "flowInstanceId",
        "stepInstanceId", "assignmentId", "reportId", "statConfigId", "statConfigVersionId",
        "statFoundationJobId", "p9RunId", "p9ResultId", "reconciliationInitialP9RunId",
        "statCsvExportId", "statXlsxExportId", "reconciliationId", "permissionRemovedReviewerId"
    };

    internal static readonly string[] ArtifactHashKeys =
    {
        "evidenceJsonArtifactId", "evidenceCsvArtifactId"
    };

    internal static readonly string[] HashKeys =
    {
        "flowPayloadHash", "flowContributionPolicyHash", "statConfigHash", "statResultId",
        "reconciliationInitialP9GenerationId",
        "reconciliationInitialP8ConfigHash", "reconciliationInitialActualGenerationId",
        "reconciliationInitialGenerationId", "reconciliationStaleActualGenerationId",
        "reconciliationActualGenerationId", "reconciliationGenerationId",
        "reconciliationStaleRecheckMarkerId", "reconciliationRecheckMarkerId"
    };

    internal sealed record CaseAccounting(int Expected, int Passed, int Failed, int NotRun);
    internal sealed record OraclePrerequisites(
        string Status, bool Ready, bool CaseAccountingVerified, CaseAccounting? CaseAccounting,
        IReadOnlyList<string> MissingPrerequisites, IReadOnlyList<string> InvalidPrerequisites);
    internal sealed record FixtureFailure(string Stage, string ExceptionType, string MessageSha256);

    internal static OraclePrerequisites AssessOraclePrerequisites(JsonNode? browserResult)
    {
        var missing = new List<string>();
        var invalid = new List<string>();
        var result = browserResult as JsonObject;
        var ids = result?["ids"] as JsonObject;
        foreach (var key in ObjectIdKeys.Concat(HashKeys).Concat(ArtifactHashKeys).Append("launchCommandId"))
        {
            var value = Text(ids?[key]);
            if (ids?[key] is null) missing.Add("ids." + key);
            else if (value is null || value.Length == 0 || value != value.Trim() ||
                     value.Any(char.IsControl) ||
                     (ObjectIdKeys.Contains(key) && !Hex(value, 24)) ||
                     (HashKeys.Concat(ArtifactHashKeys).Contains(key) && !LowerHex(value, 64))) invalid.Add("ids." + key);
        }
        var usesStaleOracle = UsesAttempt040StaleVerdictOracle(browserResult, out var staleProtocolError);
        if (staleProtocolError is not null) invalid.Add(staleProtocolError);
        if (!usesStaleOracle)
        {
            var staleGenerationId = Text(ids?["reconciliationStaleGenerationId"]);
            if (ids?["reconciliationStaleGenerationId"] is null)
                missing.Add("ids.reconciliationStaleGenerationId");
            else if (staleGenerationId is null || !LowerHex(staleGenerationId, 64))
                invalid.Add("ids.reconciliationStaleGenerationId");
        }
        var flowContributionPolicy = Text(ids?["flowContributionPolicy"]);
        if (ids?["flowContributionPolicy"] is null) missing.Add("ids.flowContributionPolicy");
        else if (!string.Equals(flowContributionPolicy, "INCLUDE", StringComparison.Ordinal))
            invalid.Add("ids.flowContributionPolicy");

        if (ids?["statConfigRevision"] is null) missing.Add("ids.statConfigRevision");
        else if (ids["statConfigRevision"] is not JsonValue revision ||
                 !revision.TryGetValue<long>(out var revisionValue) || revisionValue <= 0)
            invalid.Add("ids.statConfigRevision");
        if (Text(ids?["p9ResultId"]) is { } resultId && Text(ids?["p9RunId"]) is { } runId && resultId != runId)
            invalid.Add("ids.p9ResultId!=p9RunId");

        var accounting = ReadCaseAccounting(result?["p11Result"] as JsonObject);
        if (accounting is null) invalid.Add("p11Result.caseAccounting");
        var browserVerdict = Text(result?["verdict"]);
        if (browserVerdict is not ("PASS" or "FAIL")) invalid.Add("verdict");
        var complete = accounting is { Passed: 16, Failed: 0, NotRun: 0 };
        if (browserVerdict == "PASS" && !complete) invalid.Add("verdict.PASS_requires_exact16");
        var ready = missing.Count == 0 && invalid.Count == 0 && complete && browserVerdict == "PASS";
        var status = ready ? "READY" : invalid.Count > 0 ? "BLOCKED_INVALID_EVIDENCE" :
            missing.Count > 0 ? "BLOCKED_MISSING_PREREQUISITES" : "BLOCKED_INCOMPLETE_JOURNEY";
        return new(status, ready, accounting is not null, accounting, missing, invalid);
    }

    private static CaseAccounting? ReadCaseAccounting(JsonObject? result)
    {
        if (result?["cases"] is not JsonArray rows || rows.Count != ExpectedCases.Length) return null;
        var passed = 0;
        var failed = 0;
        var notRun = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index] is not JsonObject row || Text(row["id"]) != ExpectedCases[index]) return null;
            switch (Text(row["status"]))
            {
                case "PASS": passed++; break;
                case "FAIL": failed++; break;
                case "CHUA_CHAY": notRun++; break;
                default: return null;
            }
        }
        if (!NumberEquals(result?["expected"], 16) || !NumberEquals(result?["passed"], passed)) return null;
        // Older browser evidence has only expected/passed; validate optional counts when present.
        if (result?.ContainsKey("failed") == true && !NumberEquals(result["failed"], failed)) return null;
        if (result?.ContainsKey("notRun") == true && !NumberEquals(result["notRun"], notRun)) return null;
        return new(16, passed, failed, notRun);
    }

    internal static JsonObject BlockedOracle(OraclePrerequisites assessment)
    {
        if (assessment.Ready) throw new InvalidOperationException("A ready oracle must execute its Mongo checks.");
        return new JsonObject
        {
            ["schemaVersion"] = "P11_RESULT_DIRECT_MONGO_V1",
            ["promptId"] = "P11-04",
            ["verdict"] = "BLOCKED",
            ["checks"] = new JsonArray(),
            ["identitySha256"] = null,
            ["prerequisites"] = JsonSerializer.SerializeToNode(assessment, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ["mongoChecksExecuted"] = false,
            ["journeyPassGranted"] = false
        };
    }

    // Attempt040+ deliberately omits the stale verdict generation from browser
    // evidence. The direct Mongo oracle resolves it by the actual generation.
    // This discriminator is shared by prerequisite classification and the
    // consumer so they cannot disagree at the serialization boundary.
    internal static bool UsesAttempt040StaleVerdictOracle(
        JsonNode? browser, out string? invalidProtocol)
    {
        invalidProtocol = null;
        var proof = browser?["observations"]?["reconciliationStale"];
        if (Text(proof?["schemaVersion"]) != "P11_ATTEMPT040_STALE_REVIEW_RECOVERY_V1")
            return false;
        var ids = browser?["ids"] as JsonObject;
        if (proof is not JsonObject proofObject || ids is null ||
            ids.ContainsKey("reconciliationStaleGenerationId") ||
            !proofObject.ContainsKey("verdictGenerationId") || proof["verdictGenerationId"] is not null ||
            Text(proof["verdictIdentitySource"]) != "TRUSTED_MONGO_ORACLE_BY_ACTUAL_GENERATION" ||
            !proofObject.ContainsKey("initialReviewSupersessionCount") ||
            proof["initialReviewSupersessionCount"] is not null ||
            Text(proof["supersessionEvidenceSource"]) != "TRUSTED_MONGO_ORACLE")
        {
            invalidProtocol = "observations.reconciliationStale.oracleIdentityProtocol";
            return false;
        }
        return true;
    }

    internal static bool PhysicalCollectionScopeVerified(
        bool databaseGuarded, bool directoryGuarded, int? continuationCount,
        bool? continuationPreserved, int allowedCount, int unknownCount, int missingContinuationCount)
        => databaseGuarded && directoryGuarded && unknownCount == 0 && missingContinuationCount == 0 &&
           (continuationCount is null ? allowedCount == 0 : continuationCount > 0 && continuationPreserved == true);

    internal static FixtureFailure SanitizeFixtureFailure(Exception error)
        => new("FIXTURE", error.GetType().Name,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(error.Message))).ToLowerInvariant());


    internal sealed record JobDiagnostic(
        string Status, string? JobId, string? JobStatus, long? StateRevision, int? RetryCount,
        string? DiagnosticCode, bool DiagnosticCodeRedacted, DateTime? NextRetryAtUtc,
        DateTime? DeadlineAtUtc, DateTime? LastHeartbeatAtUtc, bool JourneyPassGranted = false);

    internal static (string JobId, string ReportId, string AssignmentId)? BoundJobIds(JsonNode? result)
    {
        var ids = (result as JsonObject)?["ids"] as JsonObject;
        var jobId = Text(ids?["statFoundationJobId"]);
        var reportId = Text(ids?["reportId"]);
        var assignmentId = Text(ids?["assignmentId"]);
        return jobId is not null && Hex(jobId, 24) && reportId is not null && Hex(reportId, 24) &&
               assignmentId is not null && Hex(assignmentId, 24)
            ? (jobId, reportId, assignmentId) : null;
    }

    internal static JobDiagnostic ProjectJobDiagnostic(
        string jobId, string status, long revision, int retries, string? code,
        DateTime? nextRetry, DateTime? deadline, DateTime? heartbeat)
    {
        var statuses = new[] { "PENDING", "RUNNING", "RETRY_WAITING", "COMPLETED", "DEAD_LETTER" };
        var codes = new[] { "FOUNDATION_DIRECT_PROJECTION_FAILED", "FOUNDATION_HEARTBEAT_FAILED",
            "FOUNDATION_WORKER_CANCELLED", "STAT_RUN_JOB_TIMEOUT", "STAT_RUN_JOB_CANCELLED" };
        if (!Hex(jobId, 24) || !statuses.Contains(status) || revision < 0 || retries < 0 ||
            new[] { nextRetry, deadline, heartbeat }.Any(value => value is not null && value.Value.Kind != DateTimeKind.Utc))
            return new("INVALID_METADATA", null, null, null, null, null, false, null, null, null);
        var publicTerminalFailure =
            code == "STAT_RUN_JOB_FAILED" && status == "DEAD_LETTER" && nextRetry is null;
        var redactCode = code is not null && !codes.Contains(code) && !publicTerminalFailure;
        return new("OBSERVED", jobId, status, revision, retries,
            redactCode ? "REDACTED_UNLISTED_CODE" : code, redactCode, nextRetry, deadline, heartbeat);
    }

    private static bool Hex(string value, int length)
        => value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    private static bool LowerHex(string value, int length)
        => value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool NumberEquals(JsonNode? node, int expected)
        => node is JsonValue value && value.TryGetValue<int>(out var number) && number == expected;
}
