using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed record WorkReportDirectGenerationContext(
    string RunId,
    string GenerationId,
    string LifecycleEventKey,
    long DirectSourceRevision,
    string DynamicFormFamilyId,
    string DynamicFormTemplateId,
    int DynamicFormVersionNo,
    string DynamicFormSchemaHash,
    string ConfigId,
    string ConfigVersionId,
    int ConfigVersionNo,
    long ConfigRevision,
    string ConfigHash,
    string CandidateChainId,
    string CatalogVersion,
    string CatalogRawSha256,
    string CatalogSemanticSha256,
    string SchemaRawSha256,
    string SchemaSemanticSha256,
    string StageLockSha256,
    string SourceMembershipSignature,
    IReadOnlyDictionary<string, WorkReportDirectContributionBinding>
        SourceContributionBindings,
    DateTime ComputedAtUtc)
{
    public WorkReportCumulativeContributionPolicy ResolveContributionPolicy(
        WorkAssignmentReport source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return SourceContributionBindings.TryGetValue(source.Id, out var binding)
            ? WorkReportCumulativeContributionPolicy.Parse(
                binding.Mode,
                binding.IsLockedFlowPolicy
                    ? null
                    : source.CumulativeContributionPolicyJson)
            : WorkReportCumulativeContributionPolicy.FromReport(source);
    }

    public WorkReportDirectProjectionPin CreatePin(WorkAssignmentReport source)
        => new()
        {
            RunId = RunId,
            GenerationId = GenerationId,
            LifecycleEventKey = LifecycleEventKey,
            SourceReportId = source.Id,
            SourcePayloadRevision = source.PayloadRevision,
            SourcePayloadHash = source.PayloadHash ?? string.Empty,
            SourceLifecycleRevision = source.LifecycleRevision,
            DirectSourceRevision = DirectSourceRevision,
            DynamicFormFamilyId = DynamicFormFamilyId,
            DynamicFormTemplateId = DynamicFormTemplateId,
            DynamicFormVersionNo = DynamicFormVersionNo,
            DynamicFormSchemaHash = DynamicFormSchemaHash,
            ConfigId = ConfigId,
            ConfigVersionId = ConfigVersionId,
            ConfigVersionNo = ConfigVersionNo,
            ConfigRevision = ConfigRevision,
            ConfigHash = ConfigHash,
            CandidateChainId = CandidateChainId,
            CatalogVersion = CatalogVersion,
            CatalogRawSha256 = CatalogRawSha256,
            CatalogSemanticSha256 = CatalogSemanticSha256,
            SchemaRawSha256 = SchemaRawSha256,
            SchemaSemanticSha256 = SchemaSemanticSha256,
            StageLockSha256 = StageLockSha256,
            SourceMembershipSignature = SourceMembershipSignature,
            ComputedAtUtc = ComputedAtUtc
        };

    public string StableObjectId(string owner, params string?[] identity)
    {
        var material = string.Join(
            "\n",
            new[] { "P9_DIRECT_ROW_V1", GenerationId, owner }
                .Concat(identity.Select(value => value ?? "<null>")));
        return StatRunCanonicalJson.HashText(material)[..24];
    }
}

public sealed record WorkReportDirectContributionBinding(
    string Mode,
    bool IsLockedFlowPolicy);
