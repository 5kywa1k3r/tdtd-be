using System.Text.Json.Serialization;
using tdtd_be.DTOs.StatisticsConfiguration;

namespace tdtd_be.DTOs.Labels;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LabelConfigPayload(
    string? Code,
    string? Name,
    string? Description,
    string? Color,
    string? GroupCode,
    string? Usage,
    string? DataType,
    string? ValueSourceType,
    IReadOnlyList<LabelValueOptionDto>? ValueOptions,
    string? ValueSourceCatalogId,
    string? ScopeType,
    string? ScopeId,
    bool IsActive);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LabelTombstonePayload();

public sealed record LabelConfigResult(
    string OwnerKind,
    string OwnerId,
    string ConfigId,
    string VersionId,
    int VersionNo,
    long Revision,
    string Status,
    string ConfigHash,
    IReadOnlyList<string> DependencyPins,
    StatConfigPermissionSet Permissions,
    LabelRow Label,
    IReadOnlyList<LabelConfigVersionSnapshotDto> Versions,
    string? ReceiptId);

public sealed record LabelConfigVersionSnapshotDto(
    string LabelId,
    string VersionId,
    string? PreviousVersionId,
    int VersionNo,
    long Revision,
    string Status,
    string ConfigHash,
    string Code,
    string Name,
    string Usage,
    string DataType,
    string ValueSourceType,
    string ScopeType,
    string? ScopeId,
    bool IsActive,
    IReadOnlyList<string> DependencyPins,
    DateTime CreatedAtUtc);

public static class LabelConfigRoutes
{
    public const string OwnerKind = StatConfigOwnerKinds.Label;
    public const string EmptyConfigHash =
        "74234e98afe7498fb5daf1f36ac2d78acc339464f950703b8c019892f982b90b";
}
