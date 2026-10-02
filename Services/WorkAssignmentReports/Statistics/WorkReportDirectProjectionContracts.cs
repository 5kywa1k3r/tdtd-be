using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using System.Collections.ObjectModel;

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
    internal IReadOnlyDictionary<string, WorkReportNativeSourcePin>? NativeSourcePins { get; private init; }
    internal string? NativeSourceOrderHash { get; private init; }

    // Explicit native capture boundary. The existing legacy generation path does
    // not opt in implicitly; L5c calls this once after freezing authorized sources.
    internal WorkReportDirectGenerationContext CaptureNativeSources(
        IEnumerable<(WorkAssignmentReport Report, WorkReportPayloadSnapshot Payload)> sources)
    {
        var pins = new Dictionary<string, WorkReportNativeSourcePin>(StringComparer.Ordinal);
        foreach (var (report, payload) in sources)
            if (!SourceContributionBindings.ContainsKey(report.Id)
                || !pins.TryAdd(report.Id, WorkReportNativeSourcePin.Capture(report, payload)))
                throw WorkReportDirectGenerationValidationException.SourceDrift();
        if (pins.Count != SourceContributionBindings.Count)
            throw WorkReportDirectGenerationValidationException.SourceDrift();
        return this with { NativeSourcePins = new ReadOnlyDictionary<string, WorkReportNativeSourcePin>(pins),
            NativeSourceOrderHash = WorkReportNativeSourcePin.Digest(pins.Values) };
    }

    internal WorkReportDirectProjectionPin CreateNativePin(WorkAssignmentReport source,
        WorkReportPayloadSnapshot payload, bool requiresTimestamp)
    {
        if (NativeSourcePins is null || !NativeSourcePins.TryGetValue(source.Id, out var captured)
            || !StatRunCanonicalJson.IsCanonicalSha256(NativeSourceOrderHash))
            throw WorkReportDirectGenerationValidationException.SourceDrift();
        captured.Validate(source, payload, requiresTimestamp);
        var pin = CreatePin(source);
        pin.SourcePayloadUpdatedAtUtc = captured.PayloadUpdatedAtUtc;
        pin.NativeSourceOrderHash = NativeSourceOrderHash;
        return pin;
    }

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
