using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

internal sealed record DynamicFlowContributionPolicySelection(
    string Policy,
    string? Warning);

/// <summary>
/// P8 configuration-only contract for statistic contribution. It binds the
/// selected mode to a locked Flow version without changing the exact P7
/// payload bytes/hash or invoking the P7 mapping runtime.
/// </summary>
internal static class DynamicFlowContributionPolicyContract
{
    internal const string Exclude = "EXCLUDE";
    internal const string Include = "INCLUDE";
    internal const string IncludeWarning =
        "DYNAMIC_FLOW_STATISTIC_CONTRIBUTION_INCLUDE_WARNING";

    internal static DynamicFlowContributionPolicySelection ResolveLockSelection(
        string? requestedPolicy,
        bool? acknowledgeWarning)
    {
        var policy = string.IsNullOrWhiteSpace(requestedPolicy)
            ? Exclude
            : requestedPolicy.Trim().ToUpperInvariant();
        if (policy is not (Exclude or Include))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FLOW_CONTRIBUTION_POLICY_INVALID,
                new
                {
                    path = "contributionPolicy",
                    reason = "DYNAMIC_FLOW_CONTRIBUTION_POLICY_INVALID"
                });
        }

        if (policy == Include && acknowledgeWarning != true)
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_CONTRIBUTION_WARNING_REQUIRED,
                new
                {
                    path = "acknowledgeContributionWarning",
                    reason = "DYNAMIC_FLOW_CONTRIBUTION_WARNING_REQUIRED",
                    warning = IncludeWarning
                });
        }

        return new DynamicFlowContributionPolicySelection(
            policy,
            policy == Include ? IncludeWarning : null);
    }

    internal static void ApplyLockedPolicy(
        DynamicFlowTemplateVersion version,
        DynamicFlowContributionPolicySelection selection)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(selection);
        version.ContributionPolicy = selection.Policy;
        version.ContributionWarning = selection.Warning;
        version.ContributionPolicyHash = ComputePolicyHash(version, selection.Policy);
    }

    internal static string ComputePolicyHash(
        DynamicFlowTemplateVersion version,
        string policy)
    {
        ArgumentNullException.ThrowIfNull(version);
        var warning = string.Equals(policy, Include, StringComparison.Ordinal)
            ? IncludeWarning
            : string.Empty;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("adapterVersion", version.AdapterVersion);
            writer.WriteString("catalogSemanticHash", version.CatalogSemanticHash);
            writer.WriteString("catalogVersion", version.CatalogVersion);
            writer.WriteString("contributionPolicy", policy);
            writer.WriteString("originFamilyId", version.OriginFamilyId ?? string.Empty);
            writer.WriteString("originVersionId", version.OriginVersionId ?? string.Empty);
            writer.WriteString("payloadHash", version.PayloadHash);
            writer.WriteString("payloadJsonSha256", Sha256(version.PayloadJson));
            writer.WriteString("rootDynamicFormTemplateId", version.RootDynamicFormTemplateId ?? string.Empty);
            writer.WriteNumber("schemaVersion", version.SchemaVersion);
            writer.WriteString("templateId", version.TemplateId);
            writer.WriteString("versionId", version.Id);
            writer.WriteNumber("versionNo", version.VersionNo);
            writer.WriteString("warning", warning);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    internal static void ValidateLockedPolicy(DynamicFlowTemplateVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var hasPolicy = !string.IsNullOrWhiteSpace(version.ContributionPolicy);
        var hasHash = !string.IsNullOrWhiteSpace(version.ContributionPolicyHash);
        var hasWarning = !string.IsNullOrWhiteSpace(version.ContributionWarning);
        if (!hasPolicy && !hasHash && !hasWarning)
            return;
        if (!hasPolicy || !hasHash)
            throw new InvalidOperationException("Locked contribution policy binding is incomplete.");

        var policy = version.ContributionPolicy!;
        if (policy is not (Exclude or Include))
            throw new InvalidOperationException("Locked contribution policy is invalid.");
        var expectedWarning = policy == Include ? IncludeWarning : null;
        if (!string.Equals(version.ContributionWarning, expectedWarning, StringComparison.Ordinal) ||
            !string.Equals(
                version.ContributionPolicyHash,
                ComputePolicyHash(version, policy),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Locked contribution policy hash is invalid.");
        }
    }

    internal static void EnsureIncludeOrigin(
        DynamicFlowTemplateVersion draft,
        DynamicFlowTemplateVersion? origin)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (origin is null ||
            !string.Equals(draft.OriginFamilyId, draft.TemplateId, StringComparison.Ordinal) ||
            !string.Equals(draft.OriginVersionId, origin.Id, StringComparison.Ordinal) ||
            !string.Equals(origin.TemplateId, draft.TemplateId, StringComparison.Ordinal) ||
            !string.Equals(origin.Status, DynamicFlowTemplateVersionStatuses.Locked, StringComparison.Ordinal) ||
            origin.IsDeleted ||
            !string.Equals(origin.ContributionPolicy, Exclude, StringComparison.Ordinal))
        {
            throw BaselineConflict();
        }

        try
        {
            ValidateLockedPolicy(origin);
        }
        catch (InvalidOperationException error)
        {
            throw BaselineConflict(error);
        }

        if (!string.Equals(draft.PayloadJson, origin.PayloadJson, StringComparison.Ordinal) ||
            !string.Equals(draft.PayloadHash, origin.PayloadHash, StringComparison.Ordinal) ||
            !string.Equals(draft.RootDynamicFormTemplateId, origin.RootDynamicFormTemplateId, StringComparison.Ordinal) ||
            draft.SchemaVersion != origin.SchemaVersion ||
            draft.AdapterVersion != origin.AdapterVersion ||
            !string.Equals(draft.CatalogVersion, origin.CatalogVersion, StringComparison.Ordinal) ||
            !string.Equals(draft.CatalogSemanticHash, origin.CatalogSemanticHash, StringComparison.Ordinal))
        {
            throw BaselineConflict();
        }
    }

    private static AppException BaselineConflict(Exception? innerException = null)
        => new(
            AppErrorCode.DYNAMIC_FLOW_CONTRIBUTION_BASELINE_CONFLICT,
            new
            {
                path = "contributionPolicy",
                reason = "DYNAMIC_FLOW_CONTRIBUTION_BASELINE_CONFLICT"
            },
            innerException: innerException);

    private static string Sha256(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)))
            .ToLowerInvariant();
}
