using System.Text.Json;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.Services.WorkAssignmentReports.Runtime;

/// <summary>
/// Pure deterministic identity contract shared by the transactional mapping
/// writer and delayed reconciler. Projectors never receive a repair-attempt key:
/// retries use the same immutable idempotency key for the committed intent.
/// </summary>
public static class DynamicFlowMappingProjectorContract
{
    public const string SchemaVersion = "P7-MAP-PROJECTOR-1";
    public const string CompletionSchemaVersion =
        "P7-MAP-PROJECTOR-COMPLETION-1";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

    public static List<DynamicFlowMappingProjectorCheckpoint> BuildPlan(
        string outboxId,
        string intentHash,
        string targetAssignmentId,
        string workReportPeriodId)
    {
        Require(outboxId, nameof(outboxId));
        Require(intentHash, nameof(intentHash));
        Require(targetAssignmentId, nameof(targetAssignmentId));
        Require(workReportPeriodId, nameof(workReportPeriodId));

        return new List<DynamicFlowMappingProjectorCheckpoint>
        {
            BuildCheckpoint(
                outboxId,
                intentHash,
                DynamicFlowMappingProjectors.QueuePeriod,
                $"period:{workReportPeriodId}"),
            BuildCheckpoint(
                outboxId,
                intentHash,
                DynamicFlowMappingProjectors.AssignmentStatus,
                $"assignment:{targetAssignmentId}"),
            BuildCheckpoint(
                outboxId,
                intentHash,
                DynamicFlowMappingProjectors.DocRolePeriod,
                $"period:{workReportPeriodId}")
        };
    }

    public static string ComputeCompletionHash(
        string intentHash,
        DynamicFlowMappingProjectorCheckpoint checkpoint,
        long repairEpoch)
    {
        Require(intentHash, nameof(intentHash));
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (repairEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repairEpoch),
                "A positive repair epoch is required.");
        }

        return ComputeHash(
            new
            {
                schemaVersion = CompletionSchemaVersion,
                intentHash,
                projector = checkpoint.Projector,
                businessKey = checkpoint.BusinessKey,
                idempotencyKey = checkpoint.IdempotencyKey,
                repairEpoch
            });
    }

    private static DynamicFlowMappingProjectorCheckpoint BuildCheckpoint(
        string outboxId,
        string intentHash,
        string projector,
        string businessKey)
        => new()
        {
            Projector = projector,
            BusinessKey = businessKey,
            IdempotencyKey = ComputeHash(
                new
                {
                    schemaVersion = SchemaVersion,
                    outboxId,
                    intentHash,
                    projector,
                    businessKey
                }),
            State =
                DynamicFlowMappingProjectorCheckpointStates.Pending
        };

    private static string ComputeHash(object value)
        => DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(
            JsonSerializer.Serialize(value, JsonOptions));

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty value is required.", name);
    }
}
