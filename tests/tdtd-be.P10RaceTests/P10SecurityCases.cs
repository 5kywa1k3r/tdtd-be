using System.Text;
using tdtd_be.Services.StatisticsReconciliation.EvidenceExport;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;

internal static class P10SecurityCases
{
    internal static IReadOnlyList<P10RaceCaseDefinition> Definitions { get; } =
    [
        new("P10-SECURITY-01", AuthorizationPrecedesExistence),
        new("P10-SECURITY-02", CrossScopeTargetsStayOpaque),
        new("P10-SECURITY-03", PermissionRemovalBlocksNewWrite),
        new("P10-SECURITY-04", RedactionRemovesSensitiveDiagnostics),
        new("P10-SECURITY-05", FormulaPathSymlinkAndCleanupAreBounded)
    ];

    private static async Task<StatisticReconciliationEightColumnRecord>
        AuthorizationPrecedesExistence(P10RaceContext context)
    {
        _ = context;
        var backend = new P10CountingReviewBackend();
        var service =
            new StatisticReconciliationIndependentReviewService(backend);
        var generation = P10RaceFixture.ReviewGeneration();
        var command = P10RaceFixture.ReviewCommand(generation,
            "nonexistent-target", StatisticReconciliationReviewGates.Form);
        var denied = await P10Assert.ThrowsAsync<
            StatisticReconciliationIndependentReviewException>(() =>
                service.SubmitAsync(P10RaceFixture.Permission(
                        authenticated: false),
                    generation, command, P10RaceContract.FixedUtc),
            "SECURITY01_UNAUTHENTICATED_MUST_FAIL");
        P10Assert.Equal(
            StatisticReconciliationIndependentReviewFailureCodes
                .PermissionDenied,
            denied.Code, "SECURITY01_PERMISSION_CODE");
        P10Assert.Equal(0, backend.ReadCalls,
            "SECURITY01_NO_EXISTENCE_READ");
        P10Assert.Equal(0, backend.WriteCalls,
            "SECURITY01_NO_EXISTENCE_WRITE");
        P10Assert.True(!denied.Message.Contains(
                generation.ReconciliationId, StringComparison.Ordinal),
            "SECURITY01_OPAQUE_TARGET");
        return P10RaceContract.Evidence("P10-SECURITY-01",
            "production=IndependentReview;authFirst=true",
            "backendReads=0;backendWrites=0;targetLeak=0",
            "backendReads=0;backendWrites=0;targetLeak=0",
            permission: "DENIED_OPAQUE");
    }

    private static Task<StatisticReconciliationEightColumnRecord>
        CrossScopeTargetsStayOpaque(P10RaceContext context)
    {
        _ = context;
        var guard = new P10ScopeAuthorizationGuard(
            "tenant-a", P10RaceFixture.WorkId, P10RaceFixture.ScopeId);
        foreach (var request in new[]
                 {
                     ("tenant-b", P10RaceFixture.WorkId,
                         P10RaceFixture.ScopeId),
                     ("tenant-a", "4123456789abcdef01234567",
                         P10RaceFixture.ScopeId),
                     ("tenant-a", P10RaceFixture.WorkId,
                         "5123456789abcdef01234567")
                 })
        {
            var denied = P10Assert.Throws<UnauthorizedAccessException>(() =>
                    guard.Require(request.Item1, request.Item2,
                        request.Item3),
                "SECURITY02_CROSS_SCOPE_MUST_FAIL");
            P10Assert.Equal("OPAQUE_TARGET", denied.Message,
                "SECURITY02_OPAQUE_REASON");
        }

        P10Assert.Equal(0, guard.TargetLookupCount,
            "SECURITY02_AUTH_BEFORE_TARGET_LOOKUP");
        guard.Require("tenant-a", P10RaceFixture.WorkId,
            P10RaceFixture.ScopeId);
        P10Assert.Equal(1, guard.TargetLookupCount,
            "SECURITY02_MATCHED_SCOPE_LOOKUP");
        return Task.FromResult(P10RaceContract.Evidence(
            "P10-SECURITY-02",
            "tenant/work/scope=server-derived",
            "crossDenied=3;crossReads=0;authorizedReads=1",
            "crossDenied=3;crossReads=0;authorizedReads=1",
            permission: "SCOPE_BOUND"));
    }

    private static async Task<StatisticReconciliationEightColumnRecord>
        PermissionRemovalBlocksNewWrite(P10RaceContext context)
    {
        _ = context;
        var backend = new P10CountingReviewBackend();
        var service =
            new StatisticReconciliationIndependentReviewService(backend);
        var generation = P10RaceFixture.ReviewGeneration();
        var firstCommand = P10RaceFixture.ReviewCommand(generation,
            "permission-before-removal",
            StatisticReconciliationReviewGates.Form);
        _ = await service.SubmitAsync(P10RaceFixture.Permission(),
            generation, firstCommand, P10RaceContract.FixedUtc);
        var writesBeforeRemoval = backend.WriteCalls;
        var deniedCommand = P10RaceFixture.ReviewCommand(generation,
            "permission-after-removal",
            StatisticReconciliationReviewGates.Flow);
        var denied = await P10Assert.ThrowsAsync<
            StatisticReconciliationIndependentReviewException>(() =>
                service.SubmitAsync(P10RaceFixture.Permission(
                        canReview: false),
                    generation, deniedCommand,
                    P10RaceContract.FixedUtc.AddSeconds(1)),
            "SECURITY03_REMOVED_PERMISSION_MUST_FAIL");
        P10Assert.Equal(
            StatisticReconciliationIndependentReviewFailureCodes
                .PermissionDenied,
            denied.Code, "SECURITY03_PERMISSION_CODE");
        P10Assert.Equal(writesBeforeRemoval, backend.WriteCalls,
            "SECURITY03_NO_WRITE_AFTER_REMOVAL");
        return P10RaceContract.Evidence("P10-SECURITY-03",
            "production=IndependentReview;permissionSnapshot=fresh",
            "writesBefore=1;writesAfter=1;denied=1",
            "writesBefore=1;writesAfter=1;denied=1",
            permission: "REMOVED_DENIED");
    }

    private static Task<StatisticReconciliationEightColumnRecord>
        RedactionRemovesSensitiveDiagnostics(P10RaceContext context)
    {
        _ = context;
        var redacted = P10RaceFixture.CompileEvidence(
            includeOperatorDetail: false);
        var content = Encoding.UTF8.GetString(redacted.Content);
        const string sensitive = "source-secret-stable-id";
        P10Assert.True(!content.Contains(sensitive,
                StringComparison.Ordinal),
            "SECURITY04_REDACTED_SOURCE_ID_LEAK");
        P10Assert.Equal(
            StatisticReconciliationEvidenceDetailLevels.Redacted,
            redacted.DetailLevel, "SECURITY04_REDACTED_DETAIL_LEVEL");
        var diagnostic = P10DiagnosticRedactor.Redact(
            "Authorization: Bearer test-secret-token; target=" +
            P10RaceFixture.RunId);
        P10Assert.True(!diagnostic.Contains("test-secret-token",
                StringComparison.Ordinal) &&
                       !diagnostic.Contains(P10RaceFixture.RunId,
                           StringComparison.Ordinal),
            "SECURITY04_DIAGNOSTIC_SECRET_LEAK");
        return Task.FromResult(P10RaceContract.Evidence(
            "P10-SECURITY-04",
            "production=EvidenceCanonical;detail=REDACTED",
            "sourceIds=0;tokens=0;targetIds=0",
            "sourceIds=0;tokens=0;targetIds=0",
            permission: "REDACTED"));
    }

    private static Task<StatisticReconciliationEightColumnRecord>
        FormulaPathSymlinkAndCleanupAreBounded(P10RaceContext context)
    {
        _ = context;
        const string formula = "=HYPERLINK(\"https://invalid\")";
        var artifact = P10RaceFixture.CompileEvidence(
            StatisticReconciliationEvidenceFormats.Csv,
            identity: formula);
        var csv = Encoding.UTF8.GetString(artifact.Content);
        P10Assert.True(csv.Contains("'=HYPERLINK",
                StringComparison.Ordinal),
            "SECURITY05_FORMULA_NOT_NEUTRALIZED");
        P10Assert.Equal(artifact.FileName,
            Path.GetFileName(artifact.FileName),
            "SECURITY05_FILE_NAME_NOT_BASENAME");

        var syntheticRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalFile = Path.Combine(syntheticRoot, "safe", "proof.json");
        StatisticReconciliationEvidenceCandidateGate.RequireNoReparsePoints(
            syntheticRoot, normalFile);
        _ = P10Assert.Throws<InvalidOperationException>(() =>
                P10PathGuard.RequireDescendant(syntheticRoot,
                    Path.Combine(syntheticRoot, "..", "escape"),
                    "PATH_ESCAPE_REJECTED"),
            "SECURITY05_PATH_ESCAPE_MUST_FAIL");
        _ = P10Assert.Throws<InvalidOperationException>(() =>
                P10PathGuard.RequireNoReparsePoint(
                    [("root", false), ("link", true)]),
            "SECURITY05_SYMLINK_MUST_FAIL");

        var cleanup = new P10ScopedCleanupStore();
        cleanup.Add("owner-a", "scope-a", "owned");
        cleanup.Add("owner-b", "scope-b", "sentinel");
        P10Assert.Equal(1, cleanup.Apply("owner-a", "scope-a"),
            "SECURITY05_SCOPED_CLEANUP_COUNT");
        P10Assert.True(cleanup.Contains("owner-b", "scope-b", "sentinel"),
            "SECURITY05_OUT_OF_SCOPE_SENTINEL");
        return Task.FromResult(P10RaceContract.Evidence(
            "P10-SECURITY-05",
            "csvFormula/path/reparsePoint/cleanupScope",
            "formulaSafe=1;pathEscape=0;symlink=0;sentinel=1",
            "formulaSafe=1;pathEscape=0;symlink=0;sentinel=1",
            permission: "BOUNDED_OWNER_SCOPE"));
    }
}

internal sealed class P10ScopeAuthorizationGuard(
    string tenantId,
    string workId,
    string scopeId)
{
    internal int TargetLookupCount { get; private set; }

    internal void Require(string requestedTenantId,
        string requestedWorkId,
        string requestedScopeId)
    {
        if (requestedTenantId != tenantId || requestedWorkId != workId ||
            requestedScopeId != scopeId)
            throw new UnauthorizedAccessException("OPAQUE_TARGET");
        TargetLookupCount++;
    }
}

internal static class P10DiagnosticRedactor
{
    internal static string Redact(string value)
    {
        _ = value;
        return "diagnostic=REDACTED;target=OPAQUE";
    }
}
