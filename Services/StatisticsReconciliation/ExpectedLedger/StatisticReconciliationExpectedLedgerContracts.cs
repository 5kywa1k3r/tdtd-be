using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

public static class StatisticReconciliationExpectedRuntimeKinds
{
    public const string Flow = "FLOW";
    public const string NonFlow = "NON_FLOW";

    public static readonly ImmutableHashSet<string> All =
        ImmutableHashSet.Create(StringComparer.Ordinal, Flow, NonFlow);
}

internal static class StatisticReconciliationExpectedLedgerInputLimits
{
    internal const int MaxSources = 10_000;
    internal const int MaxConfigurationPins = 64;
    internal const int MaxLineagePins = 128;

    internal static ImmutableArray<T> Snapshot<T>(
        IEnumerable<T>? values,
        int maximum,
        string reason,
        string path)
    {
        if (values is null)
            return default;

        var knownCount = values switch
        {
            ICollection<T> collection => collection.Count,
            IReadOnlyCollection<T> collection => collection.Count,
            _ => -1
        };
        if (knownCount < -1 || knownCount > maximum)
            throw TooLarge(reason, path, maximum);

        var builder = ImmutableArray.CreateBuilder<T>(
            knownCount >= 0 ? knownCount : Math.Min(maximum, 16));
        foreach (var value in values)
        {
            if (builder.Count == maximum)
                throw TooLarge(reason, path, maximum);
            builder.Add(value);
        }

        return builder.ToImmutable();
    }

    private static StatisticReconciliationExpectedLedgerInputException TooLarge(
        string reason,
        string path,
        int maximum)
        => new(reason, path,
            $"Collection exceeds the bounded limit of {maximum} items.");
}

/// <summary>
/// P10-T09 accepts exactly five domain inputs. Every constructor that can mint an
/// input token is internal to the server assembly; P10-T10 will provide the
/// lifecycle-aware authoritative provider. No client DTO can construct this type.
/// </summary>
public sealed class StatisticReconciliationExpectedLedgerCompileInput
{
    internal StatisticReconciliationExpectedLedgerCompileInput(
        ApprovedEffectiveReportPayloadRevisionSet approvedEffectivePayloadRevisions,
        CurrentEpochFlowMembership currentEpochFlowMembership,
        LockedP8Configuration lockedP8Configuration,
        P5P7RuntimeMappingContributionLineage runtimeMappingContributionLineage,
        ImmutableSourceIdentitySet immutableSourceIdentitySet)
    {
        ApprovedEffectivePayloadRevisions = approvedEffectivePayloadRevisions;
        CurrentEpochFlowMembership = currentEpochFlowMembership;
        LockedP8Configuration = lockedP8Configuration;
        RuntimeMappingContributionLineage = runtimeMappingContributionLineage;
        ImmutableSourceIdentitySet = immutableSourceIdentitySet;
    }

    public ApprovedEffectiveReportPayloadRevisionSet ApprovedEffectivePayloadRevisions { get; }
    public CurrentEpochFlowMembership CurrentEpochFlowMembership { get; }
    public LockedP8Configuration LockedP8Configuration { get; }
    public P5P7RuntimeMappingContributionLineage RuntimeMappingContributionLineage { get; }
    public ImmutableSourceIdentitySet ImmutableSourceIdentitySet { get; }
}

/// <summary>
/// Immutable P10-01/run header pins shared by all five domain inputs. This is a
/// cross-component coherence key, not a lifecycle decision or published run hash.
/// </summary>
public sealed record ExpectedLedgerCompilationContextPin
{
    internal ExpectedLedgerCompilationContextPin(
        string reconciliationId,
        string immutableIdentitySha256,
        string immutableHeaderSha256,
        string? tenantUnitId,
        string workId,
        string scopeAssignmentId,
        string candidateChainId,
        string candidatePromptId,
        string periodKey,
        string periodInstanceKey,
        string conceptKey,
        string grain,
        string timeAxis,
        string filterSha256,
        string dynamicFormVersionId,
        string dynamicFormSchemaSha256,
        string flowTemplateVersionId,
        string flowPayloadSha256,
        string flowInstanceId,
        string executionEpochId,
        string p8ConfigurationOwnerId,
        string p8ConfigurationBundleSha256)
        : this(
            reconciliationId,
            immutableIdentitySha256,
            immutableHeaderSha256,
            tenantUnitId,
            workId,
            scopeAssignmentId,
            candidateChainId,
            candidatePromptId,
            periodKey,
            periodInstanceKey,
            conceptKey,
            grain,
            timeAxis,
            filterSha256,
            dynamicFormVersionId,
            dynamicFormSchemaSha256,
            StatisticReconciliationExpectedRuntimeKinds.Flow,
            flowTemplateVersionId,
            flowPayloadSha256,
            flowInstanceId,
            executionEpochId,
            p8ConfigurationOwnerId,
            p8ConfigurationBundleSha256)
    {
    }

    internal ExpectedLedgerCompilationContextPin(
        string reconciliationId,
        string immutableIdentitySha256,
        string immutableHeaderSha256,
        string? tenantUnitId,
        string workId,
        string scopeAssignmentId,
        string candidateChainId,
        string candidatePromptId,
        string periodKey,
        string periodInstanceKey,
        string conceptKey,
        string grain,
        string timeAxis,
        string filterSha256,
        string dynamicFormVersionId,
        string dynamicFormSchemaSha256,
        string runtimeKind,
        string? flowTemplateVersionId,
        string? flowPayloadSha256,
        string? flowInstanceId,
        string? executionEpochId,
        string p8ConfigurationOwnerId,
        string p8ConfigurationBundleSha256)
        : this(
            reconciliationId,
            immutableIdentitySha256,
            immutableHeaderSha256,
            tenantUnitId,
            workId,
            scopeAssignmentId,
            candidateChainId,
            candidatePromptId,
            periodKey,
            periodInstanceKey,
            conceptKey,
            grain,
            timeAxis,
            filterSha256,
            dynamicFormVersionId,
            dynamicFormSchemaSha256,
            runtimeKind,
            flowTemplateVersionId,
            flowPayloadSha256,
            flowInstanceId,
            executionEpochId,
            p8ConfigurationOwnerId,
            p8ConfigurationBundleSha256,
            null,
            null,
            null,
            null,
            null)
    {
    }

    internal ExpectedLedgerCompilationContextPin(
        string reconciliationId,
        string immutableIdentitySha256,
        string immutableHeaderSha256,
        string? tenantUnitId,
        string workId,
        string scopeAssignmentId,
        string candidateChainId,
        string candidatePromptId,
        string periodKey,
        string periodInstanceKey,
        string conceptKey,
        string grain,
        string timeAxis,
        string filterSha256,
        string dynamicFormVersionId,
        string dynamicFormSchemaSha256,
        string runtimeKind,
        string? flowTemplateVersionId,
        string? flowPayloadSha256,
        string? flowInstanceId,
        string? executionEpochId,
        string p8ConfigurationOwnerId,
        string p8ConfigurationBundleSha256,
        string? sourceOwnerRunId,
        string? sourceOwnerGenerationId,
        string? sourceOwnerGenerationSha256,
        string? sourceOwnerMembershipSha256,
        long? sourceOwnerRevision)
    {
        ReconciliationId = reconciliationId;
        ImmutableIdentitySha256 = immutableIdentitySha256;
        ImmutableHeaderSha256 = immutableHeaderSha256;
        TenantUnitId = tenantUnitId;
        WorkId = workId;
        ScopeAssignmentId = scopeAssignmentId;
        CandidateChainId = candidateChainId;
        CandidatePromptId = candidatePromptId;
        PeriodKey = periodKey;
        PeriodInstanceKey = periodInstanceKey;
        ConceptKey = conceptKey;
        Grain = grain;
        TimeAxis = timeAxis;
        FilterSha256 = filterSha256;
        DynamicFormVersionId = dynamicFormVersionId;
        DynamicFormSchemaSha256 = dynamicFormSchemaSha256;
        RuntimeKind = runtimeKind;
        FlowTemplateVersionId = flowTemplateVersionId;
        FlowPayloadSha256 = flowPayloadSha256;
        FlowInstanceId = flowInstanceId;
        ExecutionEpochId = executionEpochId;
        P8ConfigurationOwnerId = p8ConfigurationOwnerId;
        P8ConfigurationBundleSha256 = p8ConfigurationBundleSha256;
        SourceOwnerRunId = sourceOwnerRunId;
        SourceOwnerGenerationId = sourceOwnerGenerationId;
        SourceOwnerGenerationSha256 = sourceOwnerGenerationSha256;
        SourceOwnerMembershipSha256 = sourceOwnerMembershipSha256;
        SourceOwnerRevision = sourceOwnerRevision;
    }

    public string ReconciliationId { get; }
    public string ImmutableIdentitySha256 { get; }
    public string ImmutableHeaderSha256 { get; }
    public string? TenantUnitId { get; }
    public string WorkId { get; }
    public string ScopeAssignmentId { get; }
    public string CandidateChainId { get; }
    public string CandidatePromptId { get; }
    public string PeriodKey { get; }
    public string PeriodInstanceKey { get; }
    public string ConceptKey { get; }
    public string Grain { get; }
    public string TimeAxis { get; }
    public string FilterSha256 { get; }
    public string DynamicFormVersionId { get; }
    public string DynamicFormSchemaSha256 { get; }
    public string RuntimeKind { get; }
    public string? FlowTemplateVersionId { get; }
    public string? FlowPayloadSha256 { get; }
    public string? FlowInstanceId { get; }
    public string? ExecutionEpochId { get; }
    public string P8ConfigurationOwnerId { get; }
    public string P8ConfigurationBundleSha256 { get; }
    public string? SourceOwnerRunId { get; }
    public string? SourceOwnerGenerationId { get; }
    public string? SourceOwnerGenerationSha256 { get; }
    public string? SourceOwnerMembershipSha256 { get; }
    public long? SourceOwnerRevision { get; }
}

public sealed record ExpectedSourceIdentityPin
{
    // Legacy test/read constructor. Trusted V2 production owners must use the
    // overload with an exact authoritative WorkAssignmentId.
    internal ExpectedSourceIdentityPin(
        string identityKey,
        string reportId,
        string workId,
        string scopeAssignmentId,
        int payloadRevision,
        string payloadOwnerSha256,
        string payloadCanonicalSha256,
        int lifecycleRevision,
        string lifecycleSha256,
        string dynamicFormVersionId,
        string flowInstanceId,
        string executionEpochId)
        : this(
            identityKey,
            reportId,
            workId,
            scopeAssignmentId,
            scopeAssignmentId,
            payloadRevision,
            payloadOwnerSha256,
            payloadCanonicalSha256,
            lifecycleRevision,
            lifecycleSha256,
            dynamicFormVersionId,
            StatisticReconciliationExpectedRuntimeKinds.Flow,
            flowInstanceId,
            executionEpochId)
    {
    }

    internal ExpectedSourceIdentityPin(
        string identityKey,
        string reportId,
        string workId,
        string scopeAssignmentId,
        string workAssignmentId,
        int payloadRevision,
        string payloadOwnerSha256,
        string payloadCanonicalSha256,
        int lifecycleRevision,
        string lifecycleSha256,
        string dynamicFormVersionId,
        string flowInstanceId,
        string executionEpochId)
        : this(
            identityKey,
            reportId,
            workId,
            scopeAssignmentId,
            workAssignmentId,
            payloadRevision,
            payloadOwnerSha256,
            payloadCanonicalSha256,
            lifecycleRevision,
            lifecycleSha256,
            dynamicFormVersionId,
            StatisticReconciliationExpectedRuntimeKinds.Flow,
            flowInstanceId,
            executionEpochId)
    {
    }

    internal ExpectedSourceIdentityPin(
        string identityKey,
        string reportId,
        string workId,
        string scopeAssignmentId,
        string workAssignmentId,
        int payloadRevision,
        string payloadOwnerSha256,
        string payloadCanonicalSha256,
        int lifecycleRevision,
        string lifecycleSha256,
        string dynamicFormVersionId,
        string runtimeKind,
        string? flowInstanceId,
        string? executionEpochId)
    {
        IdentityKey = identityKey;
        ReportId = reportId;
        WorkId = workId;
        ScopeAssignmentId = scopeAssignmentId;
        WorkAssignmentId = workAssignmentId;
        PayloadRevision = payloadRevision;
        PayloadOwnerSha256 = payloadOwnerSha256;
        PayloadCanonicalSha256 = payloadCanonicalSha256;
        LifecycleRevision = lifecycleRevision;
        LifecycleSha256 = lifecycleSha256;
        DynamicFormVersionId = dynamicFormVersionId;
        RuntimeKind = runtimeKind;
        FlowInstanceId = flowInstanceId;
        ExecutionEpochId = executionEpochId;
    }

    public string IdentityKey { get; }
    public string ReportId { get; }
    public string WorkId { get; }
    public string ScopeAssignmentId { get; }
    public string WorkAssignmentId { get; }
    public int PayloadRevision { get; }
    public string PayloadOwnerSha256 { get; }
    public string PayloadCanonicalSha256 { get; }
    public int LifecycleRevision { get; }
    public string LifecycleSha256 { get; }
    public string DynamicFormVersionId { get; }
    public string RuntimeKind { get; }
    public string? FlowInstanceId { get; }
    public string? ExecutionEpochId { get; }
}

public sealed record ApprovedEffectiveReportPayloadRevision
{
    internal ApprovedEffectiveReportPayloadRevision(
        ExpectedSourceIdentityPin identity,
        string payloadDocumentId,
        string dynamicFormVersionId,
        string dynamicFormSchemaSha256,
        string payloadCanonicalSha256,
        string payloadJson)
    {
        Identity = identity;
        PayloadDocumentId = payloadDocumentId;
        DynamicFormVersionId = dynamicFormVersionId;
        DynamicFormSchemaSha256 = dynamicFormSchemaSha256;
        PayloadCanonicalSha256 = payloadCanonicalSha256;
        PayloadJson = payloadJson;
    }

    public ExpectedSourceIdentityPin Identity { get; }
    public string PayloadDocumentId { get; }
    public string DynamicFormVersionId { get; }
    public string DynamicFormSchemaSha256 { get; }
    public string PayloadCanonicalSha256 { get; }
    public string PayloadJson { get; }
}

public sealed class ApprovedEffectiveReportPayloadRevisionSet
{
    internal ApprovedEffectiveReportPayloadRevisionSet(
        ExpectedLedgerCompilationContextPin contextPin,
        IEnumerable<ApprovedEffectiveReportPayloadRevision>? revisions)
    {
        ContextPin = contextPin;
        Revisions = StatisticReconciliationExpectedLedgerInputLimits.Snapshot(
            revisions,
            StatisticReconciliationExpectedLedgerInputLimits.MaxSources,
            StatisticReconciliationExpectedLedgerInputFailureReasons.PayloadRevisionInvalid,
            "$.approvedEffectivePayloadRevisions.revisions");
    }

    public ExpectedLedgerCompilationContextPin ContextPin { get; }
    public ImmutableArray<ApprovedEffectiveReportPayloadRevision> Revisions { get; }
}

public sealed class CurrentEpochFlowMembership
{
    internal CurrentEpochFlowMembership(
        ExpectedLedgerCompilationContextPin contextPin,
        string flowTemplateVersionId,
        string flowPayloadSha256,
        string flowInstanceId,
        string executionEpochId,
        int executionEpoch,
        long executionEpochRevision,
        IEnumerable<string>? sourceIdentityKeys)
        : this(
            contextPin,
            StatisticReconciliationExpectedRuntimeKinds.Flow,
            flowTemplateVersionId,
            flowPayloadSha256,
            flowInstanceId,
            executionEpochId,
            executionEpoch,
            executionEpochRevision,
            sourceIdentityKeys)
    {
    }

    internal CurrentEpochFlowMembership(
        ExpectedLedgerCompilationContextPin contextPin,
        string runtimeKind,
        string? flowTemplateVersionId,
        string? flowPayloadSha256,
        string? flowInstanceId,
        string? executionEpochId,
        int? executionEpoch,
        long? executionEpochRevision,
        IEnumerable<string>? sourceIdentityKeys)
    {
        ContextPin = contextPin;
        RuntimeKind = runtimeKind;
        FlowTemplateVersionId = flowTemplateVersionId;
        FlowPayloadSha256 = flowPayloadSha256;
        FlowInstanceId = flowInstanceId;
        ExecutionEpochId = executionEpochId;
        ExecutionEpoch = executionEpoch;
        ExecutionEpochRevision = executionEpochRevision;
        SourceIdentityKeys = StatisticReconciliationExpectedLedgerInputLimits.Snapshot(
            sourceIdentityKeys,
            StatisticReconciliationExpectedLedgerInputLimits.MaxSources,
            StatisticReconciliationExpectedLedgerInputFailureReasons.FlowMembershipInvalid,
            "$.currentEpochFlowMembership.sourceIdentityKeys");
    }

    public ExpectedLedgerCompilationContextPin ContextPin { get; }
    public string RuntimeKind { get; }
    public string? FlowTemplateVersionId { get; }
    public string? FlowPayloadSha256 { get; }
    public string? FlowInstanceId { get; }
    public string? ExecutionEpochId { get; }
    public int? ExecutionEpoch { get; }
    public long? ExecutionEpochRevision { get; }
    public ImmutableArray<string> SourceIdentityKeys { get; }
}

public sealed record LockedP8ConfigurationPin
{
    internal LockedP8ConfigurationPin(
        string kind,
        string ownerId,
        string configId,
        string versionId,
        int versionNo,
        long revision,
        string configSha256)
    {
        Kind = kind;
        OwnerId = ownerId;
        ConfigId = configId;
        VersionId = versionId;
        VersionNo = versionNo;
        Revision = revision;
        ConfigSha256 = configSha256;
    }

    public string Kind { get; }
    public string OwnerId { get; }
    public string ConfigId { get; }
    public string VersionId { get; }
    public int VersionNo { get; }
    public long Revision { get; }
    public string ConfigSha256 { get; }
}

public sealed class LockedP8Configuration
{
    internal LockedP8Configuration(
        ExpectedLedgerCompilationContextPin contextPin,
        string ownerId,
        string bundleOwnerSha256,
        string configurationCanonicalSha256,
        string configurationJson,
        IEnumerable<LockedP8ConfigurationPin>? pins)
    {
        ContextPin = contextPin;
        OwnerId = ownerId;
        BundleOwnerSha256 = bundleOwnerSha256;
        ConfigurationCanonicalSha256 = configurationCanonicalSha256;
        ConfigurationJson = configurationJson;
        Pins = StatisticReconciliationExpectedLedgerInputLimits.Snapshot(
            pins,
            StatisticReconciliationExpectedLedgerInputLimits.MaxConfigurationPins,
            StatisticReconciliationExpectedLedgerInputFailureReasons.ConfigurationInvalid,
            "$.lockedP8Configuration.pins");
    }

    public ExpectedLedgerCompilationContextPin ContextPin { get; }
    public string OwnerId { get; }
    public string BundleOwnerSha256 { get; }
    public string ConfigurationCanonicalSha256 { get; }
    public string ConfigurationJson { get; }
    public ImmutableArray<LockedP8ConfigurationPin> Pins { get; }
}

public sealed record ExpectedLedgerLineagePin
{
    internal ExpectedLedgerLineagePin(
        string layer,
        string ownerId,
        string versionId,
        long revision,
        string sha256)
    {
        Layer = layer;
        OwnerId = ownerId;
        VersionId = versionId;
        Revision = revision;
        Sha256 = sha256;
    }

    public string Layer { get; }
    public string OwnerId { get; }
    public string VersionId { get; }
    public long Revision { get; }
    public string Sha256 { get; }
}

public sealed class P5P7RuntimeMappingContributionLineage
{
    internal P5P7RuntimeMappingContributionLineage(
        ExpectedLedgerCompilationContextPin contextPin,
        IEnumerable<ExpectedLedgerLineagePin>? pins)
    {
        ContextPin = contextPin;
        Pins = StatisticReconciliationExpectedLedgerInputLimits.Snapshot(
            pins,
            StatisticReconciliationExpectedLedgerInputLimits.MaxLineagePins,
            StatisticReconciliationExpectedLedgerInputFailureReasons.LineageInvalid,
            "$.runtimeMappingContributionLineage.pins");
    }

    public ExpectedLedgerCompilationContextPin ContextPin { get; }
    public ImmutableArray<ExpectedLedgerLineagePin> Pins { get; }
}

public sealed class ImmutableSourceIdentitySet
{
    internal ImmutableSourceIdentitySet(
        ExpectedLedgerCompilationContextPin contextPin,
        IEnumerable<ExpectedSourceIdentityPin>? sources)
    {
        ContextPin = contextPin;
        Sources = StatisticReconciliationExpectedLedgerInputLimits.Snapshot(
            sources,
            StatisticReconciliationExpectedLedgerInputLimits.MaxSources,
            StatisticReconciliationExpectedLedgerInputFailureReasons.SourceSetRequired,
            "$.immutableSourceIdentitySet.sources");
    }

    public ExpectedLedgerCompilationContextPin ContextPin { get; }
    public ImmutableArray<ExpectedSourceIdentityPin> Sources { get; }
}

/// <summary>
/// Deeply detached T09 envelope. Fingerprints bind the five input components for
/// checkpoint integrity only; they are not the later source-set/algorithm/ledger
/// publication hashes and are never written to StatisticReconciliationRun here.
/// </summary>
public sealed record StatisticReconciliationExpectedLedgerBoundInputs(
    string SchemaVersion,
    ExpectedLedgerCompilationContextPin ContextPin,
    ImmutableArray<ApprovedEffectiveReportPayloadRevision> ApprovedEffectivePayloadRevisions,
    BoundCurrentEpochFlowMembership CurrentEpochFlowMembership,
    BoundLockedP8Configuration LockedP8Configuration,
    BoundP5P7RuntimeMappingContributionLineage RuntimeMappingContributionLineage,
    BoundImmutableSourceIdentitySet ImmutableSourceIdentitySet,
    ExpectedLedgerInputFingerprints InputFingerprints,
    StatisticReconciliationExpectedLedgerDependencyGraph DependencyGraph);

public sealed record BoundCurrentEpochFlowMembership(
    string RuntimeKind,
    string? FlowTemplateVersionId,
    string? FlowPayloadSha256,
    string? FlowInstanceId,
    string? ExecutionEpochId,
    int? ExecutionEpoch,
    long? ExecutionEpochRevision,
    ImmutableArray<string> SourceIdentityKeys);

public sealed record BoundLockedP8Configuration(
    string OwnerId,
    string BundleOwnerSha256,
    string ConfigurationCanonicalSha256,
    string CanonicalConfigurationJson,
    ImmutableArray<LockedP8ConfigurationPin> Pins);

public sealed record BoundP5P7RuntimeMappingContributionLineage(
    ImmutableArray<ExpectedLedgerLineagePin> Pins);

public sealed record BoundImmutableSourceIdentitySet(
    ImmutableArray<ExpectedSourceIdentityPin> Sources);

public sealed record ExpectedLedgerInputFingerprints(
    string SchemaVersion,
    string ContextSha256,
    string ApprovedEffectivePayloadsSha256,
    string CurrentEpochFlowMembershipSha256,
    string LockedP8ConfigurationSha256,
    string RuntimeMappingContributionLineageSha256,
    string ImmutableSourceIdentitiesSha256,
    string InputBindingSha256);

public sealed record StatisticReconciliationExpectedLedgerDependencyGraph(
    string SchemaVersion,
    ImmutableArray<string> AllowedDomainInputs,
    ImmutableArray<string> ForbiddenDependencies);

public static class StatisticReconciliationExpectedLedgerConfigurationKinds
{
    public const string DynamicForm = "DYNAMIC_FORM";
    public const string Label = "LABEL";
    public const string Field = "FIELD";
    public const string Table = "TABLE";
    public const string Basic = "BASIC";
    public const string Advanced = "ADVANCED";
    public const string Diff = "DIFF";
    public const string FlowContribution = "FLOW_CONTRIBUTION";
    public const string Readiness = "READINESS";

    public static readonly ImmutableHashSet<string> Allowed =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            DynamicForm,
            Label,
            Field,
            Table,
            Basic,
            Advanced,
            Diff,
            FlowContribution,
            Readiness);
}

public static class StatisticReconciliationExpectedLedgerLineageLayers
{
    public const string P5Runtime = "P5_RUNTIME";
    public const string P6FlowTopology = "P6_FLOW_TOPOLOGY";
    public const string P7Mapping = "P7_MAPPING";
    public const string P7Contribution = "P7_CONTRIBUTION";

    public static readonly ImmutableArray<string> Required =
    [
        P5Runtime,
        P6FlowTopology,
        P7Mapping,
        P7Contribution
    ];

    public static readonly ImmutableHashSet<string> Allowed =
        Required.ToImmutableHashSet(StringComparer.Ordinal);
}

public static class StatisticReconciliationExpectedLedgerInputFailureReasons
{
    public const string InputRequired = "P10_EXPECTED_INPUT_REQUIRED";
    public const string ContextInvalid = "P10_EXPECTED_CONTEXT_INVALID";
    public const string ContextMismatch = "P10_EXPECTED_CONTEXT_MISMATCH";
    public const string IdentifierInvalid = "P10_EXPECTED_IDENTIFIER_INVALID";
    public const string SourceSetRequired = "P10_EXPECTED_SOURCE_SET_REQUIRED";
    public const string SourceIdentityInvalid = "P10_EXPECTED_SOURCE_IDENTITY_INVALID";
    public const string SourceIdentityDuplicate = "P10_EXPECTED_SOURCE_IDENTITY_DUPLICATE";
    public const string SourceSetMismatch = "P10_EXPECTED_SOURCE_SET_MISMATCH";
    public const string PayloadRevisionInvalid = "P10_EXPECTED_PAYLOAD_REVISION_INVALID";
    public const string PayloadJsonInvalid = "P10_EXPECTED_PAYLOAD_JSON_INVALID";
    public const string PayloadAggregateTooLarge = "P10_EXPECTED_PAYLOAD_AGGREGATE_TOO_LARGE";
    public const string PayloadContentHashMismatch = "P10_EXPECTED_PAYLOAD_CONTENT_HASH_MISMATCH";
    public const string FlowMembershipInvalid = "P10_EXPECTED_FLOW_MEMBERSHIP_INVALID";
    public const string ConfigurationInvalid = "P10_EXPECTED_CONFIGURATION_INVALID";
    public const string ConfigurationKindInvalid = "P10_EXPECTED_CONFIGURATION_KIND_INVALID";
    public const string ConfigurationContentHashMismatch =
        "P10_EXPECTED_CONFIGURATION_CONTENT_HASH_MISMATCH";
    public const string LineageInvalid = "P10_EXPECTED_LINEAGE_INVALID";
    public const string LineageLayerInvalid = "P10_EXPECTED_LINEAGE_LAYER_INVALID";
    public const string LineageLayerMissing = "P10_EXPECTED_LINEAGE_LAYER_MISSING";
    public const string Sha256Invalid = "P10_EXPECTED_SHA256_INVALID";
}

public sealed class StatisticReconciliationExpectedLedgerInputException : InvalidOperationException
{
    public StatisticReconciliationExpectedLedgerInputException(
        string reason,
        string path,
        string message)
        : base(message)
    {
        Reason = reason;
        Path = path;
    }

    public string Reason { get; }
    public string Path { get; }
}
