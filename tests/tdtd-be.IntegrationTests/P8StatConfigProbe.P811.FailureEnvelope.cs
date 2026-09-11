using System.Security.Cryptography;
using System.Text;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static async Task WriteP811SealedFailureEnvelopeAsync(
        HarnessPaths paths,
        string aggregateRunKey,
        DateTime startedAtUtc,
        int activeIteration,
        string activeStage,
        int? childExitCode,
        string? childRunRoot,
        P8SecurityScanResult? postFinalSecurity,
        IReadOnlyCollection<P811SealedChildEvidence> completedChildren,
        Exception? exception)
    {
        var envelopeCt = CancellationToken.None;
        var completedAtUtc = DateTime.UtcNow;
        var childRelative = string.IsNullOrWhiteSpace(childRunRoot)
            ? null
            : P811SealedRelative(paths.WorkspaceRoot, childRunRoot);
        var missingRequiredArtifacts = string.IsNullOrWhiteSpace(childRunRoot)
            ? P811SealedArtifactNames
            : P811SealedArtifactNames
                .Where(name => !File.Exists(Path.Combine(childRunRoot, name)))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        var failureType = exception?.GetType().Name ?? "ChildExitNonZero";
        var failureMessageSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(
                    exception?.Message ?? "P8-11 child returned non-zero.")))
            .ToLowerInvariant();

        var cleanupBinding = await WriteP811SealedArtifactAsync(
            paths,
            "cleanup-manifest.json",
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = "P8-11",
                aggregateRunKey,
                state = "FAILED",
                cleanupSucceeded = false,
                activeIteration,
                activeStage,
                childExitCode,
                childRunRoot = childRelative,
                childCleanupArtifactPresent =
                    childRunRoot is not null &&
                    File.Exists(Path.Combine(
                        childRunRoot,
                        "cleanup-manifest.json")),
                childPostFinalSecurityPassed =
                    postFinalSecurity?.Passed == true,
                ownedRoots = new[]
                    {
                        P811SealedRelative(
                            paths.WorkspaceRoot,
                            paths.RunRoot)
                    }
                    .Concat(childRelative is null
                        ? Array.Empty<string>()
                        : [childRelative])
                    .ToArray(),
                completedChildren = completedChildren.Select(item => new
                {
                    item.Iteration,
                    item.ExitCode,
                    item.ChildRunKey,
                    item.RunRoot,
                    item.GatePassed
                }),
                startedAtUtc,
                completedAtUtc
            },
            envelopeCt);

        P811SealedDirectorySecurityResult aggregateSecurity;
        try
        {
            aggregateSecurity = await
                RunP811SealedAggregateSecurityScanAsync(
                    paths.RunRoot,
                    envelopeCt);
        }
        catch (Exception scanException)
        {
            aggregateSecurity = new P811SealedDirectorySecurityResult(
                false,
                0,
                [
                    new P811SealedDirectorySecurityFinding(
                        ".",
                        "SCAN_ERROR",
                        scanException.GetType().Name)
                ]);
        }

        var securityBinding = await WriteP811SealedArtifactAsync(
            paths,
            "aggregate-security-scan.json",
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = "P8-11",
                aggregateRunKey,
                scannedAfterFailureCleanup = true,
                aggregateSecurity
            },
            envelopeCt);

        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, "p8-11-gate-result.json"),
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = "P8-11",
                aggregateRunKey,
                passed = false,
                contractSatisfied = false,
                failureReason = "P8-11 sealed child failed before complete binding.",
                activeIteration,
                activeStage,
                childExitCode,
                childRunRoot = childRelative,
                failureType,
                failureMessageSha256,
                missingRequiredArtifacts,
                completedChildCount = completedChildren.Count,
                postFinalSecurityPassed =
                    postFinalSecurity?.Passed == true,
                cleanupArtifact = cleanupBinding,
                aggregateSecurityArtifact = securityBinding,
                aggregateSecurityPassed = aggregateSecurity.Passed,
                startedAtUtc,
                completedAtUtc
            },
            envelopeCt);
    }
}
