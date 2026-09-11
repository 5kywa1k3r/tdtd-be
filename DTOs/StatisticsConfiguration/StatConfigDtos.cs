using System.Text.Json.Serialization;

namespace tdtd_be.DTOs.StatisticsConfiguration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatConfigMutationEnvelope<TPayload>(
    string? CommandId,
    long? ExpectedRevision,
    string? ExpectedConfigHash,
    TPayload? Payload);

public sealed record StatConfigIdentity(
    string OwnerKind,
    string OwnerId,
    string ConfigId,
    string VersionId,
    int VersionNo,
    long Revision,
    string Status,
    string ConfigHash,
    IReadOnlyList<string> DependencyPins);

public sealed record StatConfigPermissionSet(
    bool CanReadConfig,
    bool CanManageDraft,
    bool CanLockVersion,
    bool CanViewResult,
    bool CanReadDiagnostics);

public static class StatConfigOwnerKinds
{
    public const string Label = "LABEL";
    public const string DynamicForm = "DYNAMIC_FORM";
    public const string BasicSummary = "BASIC_SUMMARY";
    public const string AdvancedSummary = "ADVANCED_SUMMARY";
    public const string Diff = "DIFF";
    public const string FlowContribution = "FLOW_CONTRIBUTION";
}

public static class StatConfigStatuses
{
    public const string Draft = "DRAFT";
    public const string Active = "ACTIVE";
    public const string Inactive = "INACTIVE";
    public const string Locked = "LOCKED";
    public const string Archived = "ARCHIVED";
    public const string Tombstoned = "TOMBSTONED";
}
