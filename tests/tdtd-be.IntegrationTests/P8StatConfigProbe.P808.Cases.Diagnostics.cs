using System.Net;
using System.Text;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP808DiagnosticCasesAsync(CancellationToken ct)
    {
        await RunP808BusinessSafeStatusCaseAsync(ct);
        await RunP808DiagnosticsAuthorizationCaseAsync(ct);
        await RunP808AdminDiagnosticsRedactionCaseAsync(ct);
    }

    private async Task RunP808BusinessSafeStatusCaseAsync(
        CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-OPS-015",
            "ordinary_a",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var jobId = _p808BaselineJobId
                            ?? throw new HarnessCaseNotRunnableException(
                                "P8 readiness baseline job is unavailable.");
                var response = await ReadP808SafeJobAsync(
                    Actor("ordinary_a"),
                    jobId,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.OK,
                    "P8 readiness ordinary safe status");
                var identity = ParseP808JobIdentity(
                    response.Json,
                    "P8 readiness ordinary safe response");
                HarnessAssert.Equal("DONE", identity.ExternalStatus,
                    "P8 ordinary safe status mismatch");
                RequireP808SafeResponse(
                    response.Json,
                    "P8 ordinary safe status",
                    "stateHash",
                    "dependencyPinsHash",
                    "requestedByUserId",
                    "diagnosticCode",
                    "diagnosticMessage",
                    "failureFingerprint",
                    "leaseUntilUtc",
                    "lastHeartbeatAtUtc",
                    "claimToken",
                    "leaseOwnerId");
                _p808DiagnosticScans.Add(new P808DiagnosticScanEvidence(
                    "P8-OPS-015",
                    "ordinary_a",
                    (int)response.StatusCode,
                    true,
                    false,
                    P808SensitiveResponseMarkers,
                    Sha256(Encoding.UTF8.GetBytes(
                        CanonicalResponse(response)))));

                return new CaseObservation(
                    "Ordinary actor read business-safe DONE status without state hash, dependency hash, requester, diagnostics, claim or lease internals.",
                    "actor=ordinary;http=200;status=DONE;safe=1;sensitiveFields=0;writes=0");
            },
            ct);
    }

    private async Task RunP808DiagnosticsAuthorizationCaseAsync(
        CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-OPS-016",
            "ordinary_a",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var jobId = _p808BaselineJobId
                            ?? throw new HarnessCaseNotRunnableException(
                                "P8 readiness baseline job is unavailable.");
                var real = await ReadP808DiagnosticsAsync(
                    Actor("ordinary_a"),
                    jobId,
                    ct);
                var missing = await ReadP808DiagnosticsAsync(
                    Actor("ordinary_a"),
                    "000000000000000000000000",
                    ct);
                ApiHarnessClient.ExpectStatus(
                    real,
                    HttpStatusCode.Forbidden,
                    "P8 readiness ordinary diagnostics real-id denial");
                ApiHarnessClient.ExpectStatus(
                    missing,
                    HttpStatusCode.Forbidden,
                    "P8 readiness ordinary diagnostics missing-id denial");
                var realCode = ApiHarnessClient.FindStringRecursive(
                    real.Json,
                    "errorCode") ?? ApiHarnessClient.FindStringRecursive(
                    real.Json,
                    "code");
                var missingCode = ApiHarnessClient.FindStringRecursive(
                    missing.Json,
                    "errorCode") ?? ApiHarnessClient.FindStringRecursive(
                    missing.Json,
                    "code");
                HarnessAssert.Equal(
                    realCode,
                    missingCode,
                    "P8 diagnostics authorization leaked existence");
                RequireP808SafeResponse(
                    real.Json,
                    "P8 ordinary diagnostics denial");
                RequireP808SafeResponse(
                    missing.Json,
                    "P8 ordinary missing diagnostics denial");
                _p808DiagnosticScans.Add(new P808DiagnosticScanEvidence(
                    "P8-OPS-016",
                    "ordinary_a",
                    (int)real.StatusCode,
                    true,
                    false,
                    P808SensitiveResponseMarkers,
                    Sha256(Encoding.UTF8.GetBytes(
                        CanonicalResponse(real)))));

                return new CaseObservation(
                    "System-admin authorization ran before readiness job existence and returned the same redacted denial for real and missing ids.",
                    "actor=ordinary;real=403;missing=403;sameCode=1;existenceLeak=0;writes=0");
            },
            ct);
    }

    private async Task RunP808AdminDiagnosticsRedactionCaseAsync(
        CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-OPS-017",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var jobId = _p808BaselineJobId
                            ?? throw new HarnessCaseNotRunnableException(
                                "P8 readiness baseline job is unavailable.");
                var response = await ReadP808DiagnosticsAsync(
                    Actor("system_admin"),
                    jobId,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.OK,
                    "P8 readiness admin diagnostics");
                HarnessAssert.True(
                    ApiHarnessClient.FindIntRecursive(
                        response.Json,
                        "stateRevision") is >= 1,
                    "P8 admin diagnostics lacks stateRevision");
                RequireP808LowerSha(
                    OptionalP808String(response.Json, "stateHash"),
                    "admin diagnostic stateHash");
                RequireP808LowerSha(
                    OptionalP808String(
                        response.Json,
                        "dependencyPinsHash"),
                    "admin diagnostic dependencyPinsHash");
                HarnessAssert.True(
                    !string.IsNullOrWhiteSpace(OptionalP808String(
                        response.Json,
                        "correlationId")),
                    "P8 admin diagnostics lacks correlationId");
                RequireP808SafeResponse(
                    response.Json,
                    "P8 admin diagnostics",
                    "claimToken",
                    "leaseOwnerId");
                _p808DiagnosticScans.Add(new P808DiagnosticScanEvidence(
                    "P8-OPS-017",
                    "system_admin",
                    (int)response.StatusCode,
                    true,
                    true,
                    P808SensitiveResponseMarkers
                        .Concat(["claimToken", "leaseOwnerId"])
                        .ToArray(),
                    Sha256(Encoding.UTF8.GetBytes(
                        CanonicalResponse(response)))));

                return new CaseObservation(
                    "System admin received correlation and integrity diagnostics while stack, connection, raw payload, source, claim token and lease owner remained redacted.",
                    "actor=systemAdmin;http=200;correlation=1;integrityHashes=2;claimToken=0;leaseOwner=0;rawPayload=0;writes=0");
            },
            ct);
    }
}

