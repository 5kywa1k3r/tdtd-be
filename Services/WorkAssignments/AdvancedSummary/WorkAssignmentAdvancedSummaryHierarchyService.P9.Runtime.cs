using MongoDB.Bson;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public sealed partial class WorkAssignmentAdvancedSummaryHierarchyService
{
    private const string P9TimeAxis = "UTC_GREGORIAN";

    private StatRunCandidateBinding RequireBuildCandidate()
        => _candidateActivation.RequireCapability(
            StatRunCapabilities.AdvancedSummary,
            StatRunRouteRegistry.AdvancedBuild);

    private StatRunCandidateBinding RequireResultCandidate()
        => _candidateActivation.RequireCapability(
            StatRunCapabilities.AdvancedSummary,
            StatRunRouteRegistry.AdvancedResult);

    private static string NormalizeBuildCommandId(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return $"p9-adv-{ObjectId.GenerateNewId()}";
        if (value.Length > 128 || value.Any(character => char.IsControl(character)))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "commandId", reason = "ADVANCED_SUMMARY_COMMAND_ID_INVALID" });
        }
        return value;
    }

    private static void EnsureExpectedConfig(
        WorkAssignmentAdvancedSummaryConfig config,
        long? expectedRevision,
        string? expectedConfigHash)
    {
        if ((expectedRevision.HasValue && expectedRevision.Value != config.Revision) ||
            (!string.IsNullOrWhiteSpace(expectedConfigHash) &&
             !string.Equals(expectedConfigHash.Trim(), config.ConfigHash, StringComparison.Ordinal)))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new
                {
                    expectedRevision,
                    expectedConfigHash,
                    actualRevision = config.Revision,
                    actualConfigHash = config.ConfigHash,
                    reason = "ADVANCED_SUMMARY_BUILD_CONFIG_PIN_MISMATCH",
                    writes = 0
                });
        }
    }

    private static string BuildNodeRequestHash(
        WorkAssignmentAdvancedSummaryConfig config,
        string grain,
        string grainKey,
        bool forceRefresh,
        string commandId)
        => StatConfigCanonicalJson.HashObject(new
        {
            commandKind = "P9_ADVANCED_SUMMARY_BUILD",
            commandId,
            configId = config.ConfigId,
            configVersionId = config.Id,
            config.VersionNo,
            config.Revision,
            config.ConfigHash,
            grain,
            grainKey,
            timeAxis = P9TimeAxis,
            forceRefresh
        });

    private static bool IsReplayOrThrow(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase? existing,
        string commandId,
        string requestHash)
    {
        if (existing is null ||
            !string.Equals(existing.BuildCommandId, commandId, StringComparison.Ordinal))
            return false;
        if (string.Equals(existing.BuildRequestHash, requestHash, StringComparison.Ordinal))
            return true;
        throw AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                commandId,
                expectedRequestHash = existing.BuildRequestHash,
                actualRequestHash = requestHash,
                reason = "ADVANCED_SUMMARY_COMMAND_REPLAY_MISMATCH",
                writes = 0
            });
    }

    private static string BuildReceiptId(
        WorkAssignmentAdvancedSummaryConfig config,
        string commandId)
        => StatConfigCanonicalJson.HashUtf8(
            $"ADVANCED_SUMMARY_BUILD\0{config.ConfigId}\0{config.Id}\0{commandId}");

    private static void ApplyCandidateLineage(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase node,
        WorkAssignmentAdvancedSummaryConfig config,
        StatRunCandidateBinding candidate)
    {
        node.ConfigId = config.ConfigId;
        node.ConfigVersionId = config.Id;
        node.ConfigVersionNo = config.VersionNo;
        node.ConfigRevision = config.Revision;
        node.ConfigHash = config.ConfigHash;
        node.DependencyPins = config.DependencyPins.OrderBy(x => x, StringComparer.Ordinal).ToList();
        node.TimeAxis = P9TimeAxis;
        node.CandidateChainId = candidate.ChainId;
        node.CandidatePromptId = candidate.PromptId;
        node.CandidateStage = candidate.Stage;
        node.CandidateCatalogRawSha256 = candidate.CatalogRawSha256;
        node.CandidateCatalogSemanticSha256 = candidate.CatalogSemanticSha256;
        node.CandidateStageLockSha256 = candidate.StageLockSha256;
    }

    private static void ApplyBuildCommand(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase node,
        WorkAssignmentAdvancedSummaryConfig config,
        string commandId,
        string requestHash)
    {
        node.BuildCommandId = commandId;
        node.BuildRequestHash = requestHash;
        node.BuildReceiptId = BuildReceiptId(config, commandId);
        node.BuildAttemptNo = checked(node.BuildAttemptNo + 1);
        node.FenceToken = checked(node.FenceToken + 1);
        node.LeaseOwner = null;
        node.LeaseExpiresAtUtc = null;
    }

    private static bool IsReusableCleanNode(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase? node,
        bool forceRefresh)
        => node is not null &&
           !forceRefresh &&
           string.Equals(
               node.Status,
               WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean,
               StringComparison.Ordinal) &&
           !node.IsDirty &&
           !string.IsNullOrWhiteSpace(node.ValueHash);

    private static void EnsureLockedConfigIntegrity(
        WorkAssignmentAdvancedSummaryConfig config)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(config.ConfigJson);
            var payload = StatConfigCanonicalJson.DeserializeStrict<
                WorkAssignmentAdvancedSummaryConfigPayload>(document.RootElement);
            var recomputed = StatConfigCanonicalJson.HashObject(new
            {
                payload,
                dependencyPins = config.DependencyPins
            });
            if (!string.Equals(recomputed, config.ConfigHash, StringComparison.Ordinal))
                throw new InvalidOperationException("hash mismatch");
        }
        catch (Exception exception) when (exception is
            System.Text.Json.JsonException or InvalidOperationException)
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new
                {
                    configId = config.ConfigId,
                    configVersionId = config.Id,
                    config.Revision,
                    config.ConfigHash,
                    reason = "ADVANCED_SUMMARY_LOCKED_CONFIG_INTEGRITY_FAILED",
                    writes = 0
                });
        }
    }

    private static string RequireBroadBuildOwnerUnitId(WorkAssignment scope)
    {
        if (!string.IsNullOrWhiteSpace(scope.IssuedByUnitId))
            return scope.IssuedByUnitId;
        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                assignmentId = scope.Id,
                reason = "ADVANCED_SUMMARY_BROAD_BUILD_OWNER_UNIT_REQUIRED",
                writes = 0
            });
    }
}
