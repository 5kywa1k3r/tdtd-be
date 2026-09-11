using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

/// <summary>
/// Pure P10-T09 boundary: canonicalize, cross-bind and detach five opaque
/// server-side inputs. The lifecycle-aware producer starts at P10-T10.
/// </summary>
public sealed class StatisticReconciliationExpectedLedgerCompiler
    : IStatisticReconciliationExpectedLedgerCompiler
{
    public const string BoundInputSchemaVersion = "P10_EXPECTED_LEDGER_BOUND_INPUT_V3";
    public const string DependencyGraphSchemaVersion = "P10_EXPECTED_LEDGER_DEPENDENCY_GRAPH_V3";
    public const string InputFingerprintSchemaVersion = "P10_EXPECTED_INPUT_BINDING_V3";

    private const int MaxSources =
        StatisticReconciliationExpectedLedgerInputLimits.MaxSources;
    private const int MaxConfigPins =
        StatisticReconciliationExpectedLedgerInputLimits.MaxConfigurationPins;
    private const int MaxLineagePins =
        StatisticReconciliationExpectedLedgerInputLimits.MaxLineagePins;
    private const int MaxPayloadBytes = 16 * 1024 * 1024;
    internal const long MaxAggregatePayloadJsonBytes = 64L * 1024 * 1024;
    private const int MaxConfigBytes = 4 * 1024 * 1024;

    private static readonly StatisticReconciliationExpectedLedgerDependencyGraph Graph =
        new(
            DependencyGraphSchemaVersion,
            [
                "IMMUTABLE_APPROVED_EFFECTIVE_REPORT_PAYLOAD_REVISIONS",
                "SERVER_DERIVED_CURRENT_EPOCH_FLOW_MEMBERSHIP",
                "LOCKED_P8_CONFIGURATION",
                "P5_P7_RUNTIME_MAPPING_CONTRIBUTION_LINEAGE",
                "IMMUTABLE_SOURCE_IDENTITY_SET"
            ],
            [
                "P9_PROJECTION_QUERY_OR_ROWS",
                "P9_AGGREGATE_QUERY_OR_ROWS",
                "P9_RESULT_QUERY_OR_ROWS",
                "P9_API_OR_EXPORT_AS_EXPECTED",
                "CLIENT_MEMBERSHIP_OR_VALUES",
                "SHARED_P9_CALCULATOR"
            ]);

    public StatisticReconciliationExpectedLedgerBoundInputs BindInputs(
        StatisticReconciliationExpectedLedgerCompileInput input)
    {
        if (input is null)
            throw Fail(StatisticReconciliationExpectedLedgerInputFailureReasons.InputRequired,
                "$", "Compiler input is required.");

        var (context, sources) = NormalizeSources(input.ImmutableSourceIdentitySet);
        var payloads = NormalizePayloads(
            input.ApprovedEffectivePayloadRevisions, context, sources.Sources);
        var membership = NormalizeMembership(
            input.CurrentEpochFlowMembership, context, sources.Sources);
        var configuration = NormalizeConfiguration(input.LockedP8Configuration, context);
        var lineage = NormalizeLineage(input.RuntimeMappingContributionLineage, context);
        var fingerprints = Fingerprint(
            context, payloads, membership, configuration, lineage, sources);

        return new(
            BoundInputSchemaVersion,
            context,
            payloads,
            membership,
            configuration,
            lineage,
            sources,
            fingerprints,
            DescribeDependencies());
    }

    public StatisticReconciliationExpectedLedgerDependencyGraph DescribeDependencies()
        => Graph;

    private static (
        ExpectedLedgerCompilationContextPin Context,
        BoundImmutableSourceIdentitySet Sources) NormalizeSources(
        ImmutableSourceIdentitySet? sourceSet)
    {
        if (sourceSet is null || sourceSet.Sources.IsDefault ||
            sourceSet.Sources.Length > MaxSources)
            throw Fail(StatisticReconciliationExpectedLedgerInputFailureReasons.SourceSetRequired,
                "$.immutableSourceIdentitySet", "A bounded source identity set is required.");

        var context = NormalizeContext(
            sourceSet.ContextPin, "$.immutableSourceIdentitySet.contextPin");
        var byIdentity = new Dictionary<string, ExpectedSourceIdentityPin>(
            StringComparer.Ordinal);
        var byRevision = new HashSet<(string, int, int)>();

        for (var index = 0; index < sourceSet.Sources.Length; index++)
        {
            var path = $"$.immutableSourceIdentitySet.sources[{index}]";
            var source = NormalizeSource(sourceSet.Sources[index], context, path);
            if (!byIdentity.TryAdd(source.IdentityKey, source) ||
                !byRevision.Add((
                    source.ReportId, source.PayloadRevision, source.LifecycleRevision)))
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons.SourceIdentityDuplicate,
                    path, "Duplicate source identity or revision.");
        }

        return (
            context,
            new BoundImmutableSourceIdentitySet(
                byIdentity.Values
                    .OrderBy(item => item.IdentityKey, StringComparer.Ordinal)
                    .ToImmutableArray()));
    }

    private static ImmutableArray<ApprovedEffectiveReportPayloadRevision> NormalizePayloads(
        ApprovedEffectiveReportPayloadRevisionSet? payloadSet,
        ExpectedLedgerCompilationContextPin context,
        ImmutableArray<ExpectedSourceIdentityPin> sources)
    {
        if (payloadSet is null || payloadSet.Revisions.IsDefault ||
            payloadSet.Revisions.Length != sources.Length)
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.PayloadRevisionInvalid,
                "$.approvedEffectivePayloadRevisions",
                "Exactly one payload revision is required for every source.");

        SameContext(payloadSet.ContextPin, context,
            "$.approvedEffectivePayloadRevisions.contextPin");
        var expected = sources.ToDictionary(x => x.IdentityKey, StringComparer.Ordinal);
        long rawPayloadBytes = 0;
        long canonicalPayloadBytes = 0;
        var result = new Dictionary<string, ApprovedEffectiveReportPayloadRevision>(
            StringComparer.Ordinal);

        for (var index = 0; index < payloadSet.Revisions.Length; index++)
        {
            var path = $"$.approvedEffectivePayloadRevisions.revisions[{index}]";
            var payload = payloadSet.Revisions[index];
            if (payload is null)
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons.PayloadRevisionInvalid,
                    path, "Payload revision is required.");

            var identity = NormalizeSource(payload.Identity, context, $"{path}.identity");
            if (!expected.TryGetValue(identity.IdentityKey, out var expectedIdentity) ||
                identity != expectedIdentity)
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons.SourceSetMismatch,
                    $"{path}.identity", "Payload identity is outside the source set.");

            Same(payload.DynamicFormVersionId, context.DynamicFormVersionId,
                $"{path}.dynamicFormVersionId", sha: false);
            Same(payload.DynamicFormSchemaSha256, context.DynamicFormSchemaSha256,
                $"{path}.dynamicFormSchemaSha256", sha: true);

            rawPayloadBytes = AccumulatePayloadBytes(
                rawPayloadBytes,
                Encoding.UTF8.GetByteCount(payload.PayloadJson ?? string.Empty),
                $"{path}.payloadJson");

            var canonical = StatisticReconciliationExpectedLedgerCanonicalizer.NormalizeObject(
                payload.PayloadJson,
                MaxPayloadBytes,
                $"{path}.payloadJson",
                StatisticReconciliationExpectedLedgerInputFailureReasons.PayloadJsonInvalid);
            canonicalPayloadBytes = AccumulatePayloadBytes(
                canonicalPayloadBytes,
                Encoding.UTF8.GetByteCount(canonical.Value),
                $"{path}.payloadJson");
            var suppliedHash = Sha(payload.PayloadCanonicalSha256,
                $"{path}.payloadCanonicalSha256");
            if (canonical.Sha256 != suppliedHash ||
                identity.PayloadCanonicalSha256 != suppliedHash)
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons
                        .PayloadContentHashMismatch,
                    $"{path}.payloadCanonicalSha256",
                    "Canonical payload content hash does not match.");

            var normalized = new ApprovedEffectiveReportPayloadRevision(
                identity,
                Id(payload.PayloadDocumentId, $"{path}.payloadDocumentId"),
                context.DynamicFormVersionId,
                context.DynamicFormSchemaSha256,
                suppliedHash,
                canonical.Value);
            if (!result.TryAdd(identity.IdentityKey, normalized))
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons
                        .SourceIdentityDuplicate,
                    $"{path}.identity.identityKey", "Duplicate payload identity.");
        }

        if (!result.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected.Keys))
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.SourceSetMismatch,
                "$.approvedEffectivePayloadRevisions",
                "Payload identities do not equal the source set.");

        return result.Values
            .OrderBy(x => x.Identity.IdentityKey, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static BoundCurrentEpochFlowMembership NormalizeMembership(
        CurrentEpochFlowMembership? membership,
        ExpectedLedgerCompilationContextPin context,
        ImmutableArray<ExpectedSourceIdentityPin> sources)
    {
        if (membership is null || membership.SourceIdentityKeys.IsDefault)
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.FlowMembershipInvalid,
                "$.currentEpochFlowMembership",
                "Prevalidated authoritative membership is required.");

        SameContext(membership.ContextPin, context,
            "$.currentEpochFlowMembership.contextPin");
        if (!string.Equals(membership.RuntimeKind, context.RuntimeKind,
                StringComparison.Ordinal))
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.ContextMismatch,
                "$.currentEpochFlowMembership.runtimeKind",
                "Membership runtime kind differs from the context.");

        if (context.RuntimeKind == StatisticReconciliationExpectedRuntimeKinds.Flow)
        {
            if (membership.ExecutionEpoch is null or <= 0 ||
                membership.ExecutionEpochRevision is null or <= 0)
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons
                        .FlowMembershipInvalid,
                    "$.currentEpochFlowMembership",
                    "FLOW membership requires positive execution epoch pins.");
            Same(membership.FlowTemplateVersionId, context.FlowTemplateVersionId!,
                "$.currentEpochFlowMembership.flowTemplateVersionId", sha: false);
            Same(membership.FlowPayloadSha256, context.FlowPayloadSha256!,
                "$.currentEpochFlowMembership.flowPayloadSha256", sha: true);
            Same(membership.FlowInstanceId, context.FlowInstanceId!,
                "$.currentEpochFlowMembership.flowInstanceId", sha: false);
            Same(membership.ExecutionEpochId, context.ExecutionEpochId!,
                "$.currentEpochFlowMembership.executionEpochId", sha: false);
        }
        else if (membership.FlowTemplateVersionId is not null ||
                 membership.FlowPayloadSha256 is not null ||
                 membership.FlowInstanceId is not null ||
                 membership.ExecutionEpochId is not null ||
                 membership.ExecutionEpoch is not null ||
                 membership.ExecutionEpochRevision is not null)
        {
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons
                    .FlowMembershipInvalid,
                "$.currentEpochFlowMembership",
                "NON_FLOW membership requires canonical not-applicable Flow pins.");
        }

        if (membership.SourceIdentityKeys.Length != sources.Length)
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.SourceSetMismatch,
                "$.currentEpochFlowMembership.sourceIdentityKeys",
                "Membership cardinality does not match the source set.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < membership.SourceIdentityKeys.Length; index++)
        {
            var key = Id(membership.SourceIdentityKeys[index],
                $"$.currentEpochFlowMembership.sourceIdentityKeys[{index}]");
            if (!keys.Add(key))
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons
                        .SourceIdentityDuplicate,
                    $"$.currentEpochFlowMembership.sourceIdentityKeys[{index}]",
                    "Duplicate membership identity.");
        }
        if (!keys.SetEquals(sources.Select(x => x.IdentityKey)))
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.SourceSetMismatch,
                "$.currentEpochFlowMembership.sourceIdentityKeys",
                "Membership does not exactly equal the source set.");

        return new(
            context.RuntimeKind,
            context.FlowTemplateVersionId,
            context.FlowPayloadSha256,
            context.FlowInstanceId,
            context.ExecutionEpochId,
            membership.ExecutionEpoch,
            membership.ExecutionEpochRevision,
            keys.OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray());
    }
    private static BoundLockedP8Configuration NormalizeConfiguration(
        LockedP8Configuration? configuration,
        ExpectedLedgerCompilationContextPin context)
    {
        if (configuration is null || configuration.Pins.IsDefaultOrEmpty ||
            configuration.Pins.Length is < 1 or > MaxConfigPins)
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.ConfigurationInvalid,
                "$.lockedP8Configuration",
                "A bounded locked P8 configuration is required.");

        SameContext(configuration.ContextPin, context,
            "$.lockedP8Configuration.contextPin");
        Same(configuration.OwnerId, context.P8ConfigurationOwnerId,
            "$.lockedP8Configuration.ownerId", sha: false);
        Same(configuration.BundleOwnerSha256, context.P8ConfigurationBundleSha256,
            "$.lockedP8Configuration.bundleOwnerSha256", sha: true);

        var canonical = StatisticReconciliationExpectedLedgerCanonicalizer.NormalizeObject(
            configuration.ConfigurationJson,
            MaxConfigBytes,
            "$.lockedP8Configuration.configurationJson",
            StatisticReconciliationExpectedLedgerInputFailureReasons.ConfigurationInvalid);
        var suppliedHash = Sha(
            configuration.ConfigurationCanonicalSha256,
            "$.lockedP8Configuration.configurationCanonicalSha256");
        if (canonical.Sha256 != suppliedHash)
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons
                    .ConfigurationContentHashMismatch,
                "$.lockedP8Configuration.configurationCanonicalSha256",
                "Canonical configuration content hash does not match.");

        var pins = new Dictionary<(string, string, string), LockedP8ConfigurationPin>();
        for (var index = 0; index < configuration.Pins.Length; index++)
        {
            var path = $"$.lockedP8Configuration.pins[{index}]";
            var pin = configuration.Pins[index];
            if (pin is null || pin.VersionNo <= 0 || pin.Revision <= 0)
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons.ConfigurationInvalid,
                    path, "Positive configuration version and revision are required.");

            var kind = Id(pin.Kind, $"{path}.kind");
            if (!StatisticReconciliationExpectedLedgerConfigurationKinds.Allowed
                    .Contains(kind))
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons
                        .ConfigurationKindInvalid,
                    $"{path}.kind", "Unknown P8 configuration kind.");

            var owner = Id(pin.OwnerId, $"{path}.ownerId");
            if (owner != context.P8ConfigurationOwnerId)
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons.ContextMismatch,
                    $"{path}.ownerId", "Configuration owner differs from the context.");

            var normalized = new LockedP8ConfigurationPin(
                kind,
                owner,
                Id(pin.ConfigId, $"{path}.configId"),
                Id(pin.VersionId, $"{path}.versionId"),
                pin.VersionNo,
                pin.Revision,
                Sha(pin.ConfigSha256, $"{path}.configSha256"));
            if (!pins.TryAdd(
                    (normalized.Kind, normalized.ConfigId, normalized.VersionId),
                    normalized))
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons.ConfigurationInvalid,
                    path, "Duplicate configuration pin.");
        }

        return new(
            context.P8ConfigurationOwnerId,
            context.P8ConfigurationBundleSha256,
            suppliedHash,
            canonical.Value,
            pins.Values
                .OrderBy(x => x.Kind, StringComparer.Ordinal)
                .ThenBy(x => x.ConfigId, StringComparer.Ordinal)
                .ThenBy(x => x.VersionId, StringComparer.Ordinal)
                .ToImmutableArray());
    }

    private static BoundP5P7RuntimeMappingContributionLineage NormalizeLineage(
        P5P7RuntimeMappingContributionLineage? lineage,
        ExpectedLedgerCompilationContextPin context)
    {
        if (lineage is null || lineage.Pins.IsDefaultOrEmpty ||
            lineage.Pins.Length is < 1 or > MaxLineagePins)
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.LineageInvalid,
                "$.runtimeMappingContributionLineage",
                "A bounded P5-P7 lineage is required.");

        SameContext(lineage.ContextPin, context,
            "$.runtimeMappingContributionLineage.contextPin");
        var pins = new Dictionary<(string, string, string), ExpectedLedgerLineagePin>();

        for (var index = 0; index < lineage.Pins.Length; index++)
        {
            var path = $"$.runtimeMappingContributionLineage.pins[{index}]";
            var pin = lineage.Pins[index];
            if (pin is null || pin.Revision <= 0)
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons.LineageInvalid,
                    path, "Positive lineage revision is required.");

            var layer = Id(pin.Layer, $"{path}.layer");
            if (!StatisticReconciliationExpectedLedgerLineageLayers.Allowed
                    .Contains(layer))
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons
                        .LineageLayerInvalid,
                    $"{path}.layer", "Unknown P5-P7 lineage layer.");

            var normalized = new ExpectedLedgerLineagePin(
                layer,
                Id(pin.OwnerId, $"{path}.ownerId"),
                Id(pin.VersionId, $"{path}.versionId"),
                pin.Revision,
                Sha(pin.Sha256, $"{path}.sha256"));
            if (!pins.TryAdd(
                    (normalized.Layer, normalized.OwnerId, normalized.VersionId),
                    normalized))
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons.LineageInvalid,
                    path, "Duplicate lineage pin.");
        }

        var present = pins.Values.Select(x => x.Layer).ToHashSet(StringComparer.Ordinal);
        var required = context.RuntimeKind ==
                StatisticReconciliationExpectedRuntimeKinds.Flow
            ? StatisticReconciliationExpectedLedgerLineageLayers.Required
            : ImmutableArray.Create(
                StatisticReconciliationExpectedLedgerLineageLayers.P5Runtime,
                StatisticReconciliationExpectedLedgerLineageLayers.P7Contribution);
        var missing = required
            .Where(layer => !present.Contains(layer))
            .ToArray();
        if (context.RuntimeKind ==
                StatisticReconciliationExpectedRuntimeKinds.NonFlow &&
            present.Any(layer =>
                layer == StatisticReconciliationExpectedLedgerLineageLayers.P6FlowTopology ||
                layer == StatisticReconciliationExpectedLedgerLineageLayers.P7Mapping))
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.LineageInvalid,
                "$.runtimeMappingContributionLineage.pins",
                "NON_FLOW lineage cannot contain Flow topology or mapping pins.");
        if (missing.Length > 0)
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.LineageLayerMissing,
                "$.runtimeMappingContributionLineage.pins",
                $"Missing lineage layers: {string.Join(",", missing)}.");

        return new(
            pins.Values
                .OrderBy(x => x.Layer, StringComparer.Ordinal)
                .ThenBy(x => x.OwnerId, StringComparer.Ordinal)
                .ThenBy(x => x.VersionId, StringComparer.Ordinal)
                .ToImmutableArray());
    }

    private static ExpectedLedgerCompilationContextPin NormalizeContext(
        ExpectedLedgerCompilationContextPin? context,
        string path)
    {
        if (context is null)
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.ContextInvalid,
                path, "Compilation context is required.");

        var runtimeKind = Id(context.RuntimeKind, $"{path}.runtimeKind");
        if (!StatisticReconciliationExpectedRuntimeKinds.All.Contains(runtimeKind))
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.ContextInvalid,
                $"{path}.runtimeKind",
                "Runtime kind must be FLOW or NON_FLOW.");

        string? flowTemplateVersionId;
        string? flowPayloadSha256;
        string? flowInstanceId;
        string? executionEpochId;
        if (runtimeKind == StatisticReconciliationExpectedRuntimeKinds.Flow)
        {
            flowTemplateVersionId = Id(
                context.FlowTemplateVersionId, $"{path}.flowTemplateVersionId");
            flowPayloadSha256 = Sha(
                context.FlowPayloadSha256, $"{path}.flowPayloadSha256");
            flowInstanceId = Id(context.FlowInstanceId, $"{path}.flowInstanceId");
            executionEpochId = Id(context.ExecutionEpochId, $"{path}.executionEpochId");
        }
        else
        {
            if (context.FlowTemplateVersionId is not null ||
                context.FlowPayloadSha256 is not null ||
                context.FlowInstanceId is not null ||
                context.ExecutionEpochId is not null)
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons.ContextInvalid,
                    path,
                    "NON_FLOW context requires canonical not-applicable Flow pins.");
            flowTemplateVersionId = null;
            flowPayloadSha256 = null;
            flowInstanceId = null;
            executionEpochId = null;
        }

        var hasSourceOwner = context.SourceOwnerRunId is not null ||
                             context.SourceOwnerGenerationId is not null ||
                             context.SourceOwnerGenerationSha256 is not null ||
                             context.SourceOwnerMembershipSha256 is not null ||
                             context.SourceOwnerRevision is not null;
        string? sourceOwnerRunId = null;
        string? sourceOwnerGenerationId = null;
        string? sourceOwnerGenerationSha256 = null;
        string? sourceOwnerMembershipSha256 = null;
        long? sourceOwnerRevision = null;
        if (hasSourceOwner)
        {
            if (context.SourceOwnerRevision is null or <= 0)
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons.ContextInvalid,
                    $"{path}.sourceOwnerRevision",
                    "Positive source-owner revision required.");
            sourceOwnerRunId = Id(
                context.SourceOwnerRunId, $"{path}.sourceOwnerRunId");
            sourceOwnerGenerationId = Id(
                context.SourceOwnerGenerationId,
                $"{path}.sourceOwnerGenerationId");
            sourceOwnerGenerationSha256 = Sha(
                context.SourceOwnerGenerationSha256,
                $"{path}.sourceOwnerGenerationSha256");
            sourceOwnerMembershipSha256 = Sha(
                context.SourceOwnerMembershipSha256,
                $"{path}.sourceOwnerMembershipSha256");
            sourceOwnerRevision = context.SourceOwnerRevision;
        }

        return new(
            Id(context.ReconciliationId, $"{path}.reconciliationId"),
            Sha(context.ImmutableIdentitySha256, $"{path}.immutableIdentitySha256"),
            Sha(context.ImmutableHeaderSha256, $"{path}.immutableHeaderSha256"),
            OptionalId(context.TenantUnitId, $"{path}.tenantUnitId"),
            Id(context.WorkId, $"{path}.workId"),
            Id(context.ScopeAssignmentId, $"{path}.scopeAssignmentId"),
            Id(context.CandidateChainId, $"{path}.candidateChainId"),
            Id(context.CandidatePromptId, $"{path}.candidatePromptId"),
            Id(context.PeriodKey, $"{path}.periodKey"),
            Id(context.PeriodInstanceKey, $"{path}.periodInstanceKey"),
            Id(context.ConceptKey, $"{path}.conceptKey"),
            Id(context.Grain, $"{path}.grain"),
            Id(context.TimeAxis, $"{path}.timeAxis"),
            Sha(context.FilterSha256, $"{path}.filterSha256"),
            Id(context.DynamicFormVersionId, $"{path}.dynamicFormVersionId"),
            Sha(context.DynamicFormSchemaSha256, $"{path}.dynamicFormSchemaSha256"),
            runtimeKind,
            flowTemplateVersionId,
            flowPayloadSha256,
            flowInstanceId,
            executionEpochId,
            Id(context.P8ConfigurationOwnerId, $"{path}.p8ConfigurationOwnerId"),
            Sha(context.P8ConfigurationBundleSha256,
                $"{path}.p8ConfigurationBundleSha256"),
            sourceOwnerRunId,
            sourceOwnerGenerationId,
            sourceOwnerGenerationSha256,
            sourceOwnerMembershipSha256,
            sourceOwnerRevision);
    }
    private static void SameContext(
        ExpectedLedgerCompilationContextPin? candidate,
        ExpectedLedgerCompilationContextPin expected,
        string path)
    {
        if (NormalizeContext(candidate, path) != expected)
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.ContextMismatch,
                path, "All five inputs must share one exact context.");
    }

    private static ExpectedSourceIdentityPin NormalizeSource(
        ExpectedSourceIdentityPin? source,
        ExpectedLedgerCompilationContextPin context,
        string path)
    {
        if (source is null ||
            source.PayloadRevision <= 0 ||
            source.LifecycleRevision <= 0)
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.SourceIdentityInvalid,
                path, "Positive payload/lifecycle revisions are required.");

        var runtimeKind = Id(source.RuntimeKind, $"{path}.runtimeKind");
        if (runtimeKind != context.RuntimeKind)
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.ContextMismatch,
                $"{path}.runtimeKind",
                "Source runtime kind differs from the context.");
        string? flowInstanceId;
        string? executionEpochId;
        if (runtimeKind == StatisticReconciliationExpectedRuntimeKinds.Flow)
        {
            flowInstanceId = Id(source.FlowInstanceId, $"{path}.flowInstanceId");
            executionEpochId = Id(source.ExecutionEpochId, $"{path}.executionEpochId");
        }
        else
        {
            if (source.FlowInstanceId is not null || source.ExecutionEpochId is not null)
                throw Fail(
                    StatisticReconciliationExpectedLedgerInputFailureReasons
                        .SourceIdentityInvalid,
                    path,
                    "NON_FLOW source identity requires not-applicable Flow pins.");
            flowInstanceId = null;
            executionEpochId = null;
        }

        var normalized = new ExpectedSourceIdentityPin(
            Id(source.IdentityKey, $"{path}.identityKey"),
            Id(source.ReportId, $"{path}.reportId"),
            Id(source.WorkId, $"{path}.workId"),
            Id(source.ScopeAssignmentId, $"{path}.scopeAssignmentId"),
            Id(source.WorkAssignmentId, $"{path}.workAssignmentId"),
            source.PayloadRevision,
            Sha(source.PayloadOwnerSha256, $"{path}.payloadOwnerSha256"),
            Sha(source.PayloadCanonicalSha256, $"{path}.payloadCanonicalSha256"),
            source.LifecycleRevision,
            Sha(source.LifecycleSha256, $"{path}.lifecycleSha256"),
            Id(source.DynamicFormVersionId, $"{path}.dynamicFormVersionId"),
            runtimeKind,
            flowInstanceId,
            executionEpochId);

        if (normalized.WorkId != context.WorkId ||
            normalized.ScopeAssignmentId != context.ScopeAssignmentId ||
            normalized.DynamicFormVersionId != context.DynamicFormVersionId ||
            normalized.FlowInstanceId != context.FlowInstanceId ||
            normalized.ExecutionEpochId != context.ExecutionEpochId)
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.ContextMismatch,
                path, "Source identity differs from the context.");

        return normalized;
    }

    private static ExpectedLedgerInputFingerprints Fingerprint(
        ExpectedLedgerCompilationContextPin context,
        ImmutableArray<ApprovedEffectiveReportPayloadRevision> payloads,
        BoundCurrentEpochFlowMembership membership,
        BoundLockedP8Configuration configuration,
        BoundP5P7RuntimeMappingContributionLineage lineage,
        BoundImmutableSourceIdentitySet sources)
    {
        var contextHash = H("P10_EXPECTED_CONTEXT_V3", ContextFields(context));
        var sourceHash = H(
            "P10_EXPECTED_SOURCE_IDENTITIES_V2",
            new[] { contextHash }.Concat(sources.Sources.Select(SourceFingerprint)));
        var payloadHash = H(
            "P10_EXPECTED_PAYLOAD_REVISIONS_V1",
            new[] { contextHash }.Concat(payloads.Select(x => H(
                "P10_EXPECTED_PAYLOAD_REVISION_V1",
                SourceFingerprint(x.Identity),
                x.PayloadDocumentId,
                x.DynamicFormVersionId,
                x.DynamicFormSchemaSha256,
                x.PayloadCanonicalSha256))));
        var membershipHash = H(
            "P10_EXPECTED_RUNTIME_MEMBERSHIP_V2",
            new[]
            {
                contextHash,
                membership.RuntimeKind,
                membership.FlowTemplateVersionId ?? "~",
                membership.FlowPayloadSha256 ?? "~",
                membership.FlowInstanceId ?? "~",
                membership.ExecutionEpochId ?? "~",
                N(membership.ExecutionEpoch),
                N(membership.ExecutionEpochRevision)
            }.Concat(membership.SourceIdentityKeys));
        var configHash = H(
            "P10_EXPECTED_P8_CONFIGURATION_V1",
            new[]
            {
                contextHash,
                configuration.OwnerId,
                configuration.BundleOwnerSha256,
                configuration.ConfigurationCanonicalSha256
            }.Concat(configuration.Pins.Select(x => H(
                "P10_EXPECTED_P8_PIN_V1",
                x.Kind, x.OwnerId, x.ConfigId, x.VersionId,
                N(x.VersionNo), N(x.Revision), x.ConfigSha256))));
        var lineageHash = H(
            "P10_EXPECTED_P5_P7_LINEAGE_V1",
            new[] { contextHash }.Concat(lineage.Pins.Select(x => H(
                "P10_EXPECTED_LINEAGE_PIN_V1",
                x.Layer, x.OwnerId, x.VersionId, N(x.Revision), x.Sha256))));
        var bindingHash = H(
            InputFingerprintSchemaVersion,
            contextHash,
            payloadHash,
            membershipHash,
            configHash,
            lineageHash,
            sourceHash);

        return new(
            InputFingerprintSchemaVersion,
            contextHash,
            payloadHash,
            membershipHash,
            configHash,
            lineageHash,
            sourceHash,
            bindingHash);
    }

    private static IEnumerable<string> ContextFields(
        ExpectedLedgerCompilationContextPin x)
    {
        yield return x.ReconciliationId;
        yield return x.ImmutableIdentitySha256;
        yield return x.ImmutableHeaderSha256;
        yield return x.TenantUnitId ?? string.Empty;
        yield return x.WorkId;
        yield return x.ScopeAssignmentId;
        yield return x.CandidateChainId;
        yield return x.CandidatePromptId;
        yield return x.PeriodKey;
        yield return x.PeriodInstanceKey;
        yield return x.ConceptKey;
        yield return x.Grain;
        yield return x.TimeAxis;
        yield return x.FilterSha256;
        yield return x.DynamicFormVersionId;
        yield return x.DynamicFormSchemaSha256;
        yield return x.RuntimeKind;
        yield return x.FlowTemplateVersionId ?? "~";
        yield return x.FlowPayloadSha256 ?? "~";
        yield return x.FlowInstanceId ?? "~";
        yield return x.ExecutionEpochId ?? "~";
        yield return x.P8ConfigurationOwnerId;
        yield return x.P8ConfigurationBundleSha256;
        yield return x.SourceOwnerRunId ?? "~";
        yield return x.SourceOwnerGenerationId ?? "~";
        yield return x.SourceOwnerGenerationSha256 ?? "~";
        yield return x.SourceOwnerMembershipSha256 ?? "~";
        yield return N(x.SourceOwnerRevision);
    }

    private static string SourceFingerprint(ExpectedSourceIdentityPin x)
        => H(
            "P10_EXPECTED_SOURCE_IDENTITY_V2",
            x.IdentityKey,
            x.ReportId,
            x.WorkId,
            x.ScopeAssignmentId,
            x.WorkAssignmentId,
            N(x.PayloadRevision),
            x.PayloadOwnerSha256,
            x.PayloadCanonicalSha256,
            N(x.LifecycleRevision),
            x.LifecycleSha256,
            x.DynamicFormVersionId,
            x.RuntimeKind,
            x.FlowInstanceId ?? "~",
            x.ExecutionEpochId ?? "~");

    private static string H(string domain, params string[] fields)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(domain, fields);

    private static string H(string domain, IEnumerable<string> fields)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(domain, fields);

    private static string N(long? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "~";

    internal static long AccumulatePayloadBytes(long current, long next, string path)
    {
        if (current < 0 ||
            next < 0 ||
            current > MaxAggregatePayloadJsonBytes - next)
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons
                    .PayloadAggregateTooLarge,
                path,
                $"Aggregate payload JSON exceeds {MaxAggregatePayloadJsonBytes} bytes.");

        return current + next;
    }

    private static void Same(string? candidate, string expected, string path, bool sha)
    {
        var normalized = sha ? Sha(candidate, path) : Id(candidate, path);
        if (normalized != expected)
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.ContextMismatch,
                path, "Input pin differs from the context.");
    }

    private static string Id(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 512 ||
            value != value.Trim() ||
            !value.IsNormalized(NormalizationForm.FormC) ||
            value.Any(c => char.IsControl(c) || c == '\u001f'))
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.IdentifierInvalid,
                path, "Canonical bounded identifier required.");
        return value;
    }

    private static string? OptionalId(string? value, string path)
        => value is null ? null : Id(value, path);

    private static string Sha(string? value, string path)
    {
        if (value is null ||
            value.Length != 64 ||
            value.Any(c => c is not (>= '0' and <= '9') and
                               not (>= 'a' and <= 'f')))
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.Sha256Invalid,
                path, "Lowercase SHA-256 required.");
        return value;
    }

    private static StatisticReconciliationExpectedLedgerInputException Fail(
        string reason,
        string path,
        string message)
        => new(reason, path, message);
}
