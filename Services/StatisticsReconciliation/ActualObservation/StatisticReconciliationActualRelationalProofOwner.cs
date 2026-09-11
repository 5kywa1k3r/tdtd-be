using System.Collections.Immutable;
using System.Text.Json;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualRelationalProofSchemas
{
    internal const string Command =
        "P10_ACTUAL_RELATIONAL_PROOF_COMMAND_V2";
    internal const string Facts =
        "P10_ACTUAL_RELATIONAL_PROOF_FACTS_V3";
    internal const string Binding =
        "P10_ACTUAL_RELATIONAL_PROOF_BINDING_V3";
    internal const string Resolution =
        "P10_ACTUAL_RELATIONAL_PROOF_RESOLUTION_V3";
}

internal static class StatisticReconciliationActualRelationalProofStates
{
    internal const string Complete = "COMPLETE";
    internal const string Incomplete = "INCOMPLETE";
}

/// <summary>
/// Server-only carrier. Every value-bearing input is a capture produced by an
/// authoritative owner; callers cannot supply a compact proof or digest.
/// </summary>
internal sealed record StatisticReconciliationActualRelationalProofCommand(
    string SchemaVersion,
    StatisticReconciliationActualSummaryPlanBinding SummaryPlan,
    StatisticReconciliationActualTrustedCaptureMaterial Material,
    ActualSourceMembershipCapture Source,
    ActualDirectProjectionCapture Direct,
    ActualAggregateCapture Aggregate,
    ActualBasicResultObservation? Basic,
    ActualAdvancedCapture? Advanced,
    ActualP9DiffCapture? Diff,
    StatisticReconciliationActualExtendedRawSourceResolution ExtendedRawSource,
    StatisticReconciliationActualExtendedRawSourceOwnerParityProof ExtendedOwnerParity,
    StatisticReconciliationActualApiCapture? Api,
    StatisticReconciliationActualExportCapture Export);

/// <summary>
/// Complete scalar fact set from one authoritative resolution. It intentionally
/// retains independent raw, P9-owner, and API/export proof roots rather than
/// collapsing them into a single opaque digest.
/// </summary>
internal sealed record StatisticReconciliationActualRelationalProofFacts(
    string SchemaVersion,
    string SummaryPlanBindingSha256,
    string CentralProjectedPlanSha256,
    string CentralPlanProjectionProofSha256,
    string SourceCaptureSha256,
    string DirectCaptureSha256,
    string AggregateCaptureSha256,
    string BasicCaptureSha256,
    string AdvancedCaptureSha256,
    string DiffCaptureSha256,
    string? ApiCaptureSha256,
    string ExportCaptureSha256,
    string RawProofSha256,
    string RawSourceManifestSha256,
    int RawSourceCount,
    string RawAtomManifestSha256,
    int RawAtomCount,
    string RawDoubleCollectProofSha256,
    string SummaryParityProofSha256,
    string SummaryParityRawManifestSha256,
    int SummaryParityRawAtomCount,
    string SummaryParityOwnerManifestSha256,
    int SummaryParityOwnerItemCount,
    string SummaryParityRelationManifestSha256,
    int SummaryParityRelationCount,
    string DirectParityProofSha256,
    string DirectParityOwnerManifestSha256,
    int DirectParityOwnerItemCount,
    string DirectParityRelationManifestSha256,
    int DirectParityRelationCount,
    string ExtendedRawResolutionProofSha256,
    string ExtendedAdvancedProofSha256,
    string ExtendedAdvancedSchemaOptionBindingSha256,
    string ExtendedAdvancedDescriptorProjectionManifestSha256,
    int ExtendedAdvancedDescriptorProjectionCount,
    string ExtendedAdvancedTypedAtomManifestSha256,
    int ExtendedAdvancedTypedAtomCount,
    string ExtendedAdvancedDescriptorContributionManifestSha256,
    int ExtendedAdvancedDescriptorContributionCount,
    string ExtendedDiffProofSha256,
    string ExtendedDiffPeriodBindingSha256,
    string ExtendedDiffTypedAtomManifestSha256,
    int ExtendedDiffTypedAtomCount,
    string ExtendedDiffSourcePairManifestSha256,
    int ExtendedDiffSourcePairCount,
    string ExtendedPartitionDoubleCollectManifestSha256,
    int ExtendedPartitionDoubleCollectCount,
    string ExtendedOwnerParityProofSha256,
    string ExtendedOwnerDescriptorManifestSha256,
    int ExtendedOwnerDescriptorCount,
    string ExtendedOwnerTypedAtomManifestSha256,
    int ExtendedOwnerTypedAtomCount,
    string ExtendedOwnerItemManifestSha256,
    int ExtendedOwnerItemCount,
    string ExtendedOwnerRelationManifestSha256,
    int ExtendedOwnerRelationCount,
    string CrossViewAuthorizationRelationSha256,
    string CrossViewResolutionSha256,
    string CrossViewProofSha256,
    string CrossViewCompatibilityProofSha256,
    string CrossViewOriginalApiSemanticSha256,
    string CrossViewOriginalExportSemanticSha256,
    string SemanticSha256);

/// <summary>
/// Durable V7-ready binding. First and second facts must be exactly equal; the
/// double proof closes owner mutations between the two independent rounds.
/// </summary>
internal sealed record StatisticReconciliationActualRelationalProofBinding(
    string SchemaVersion,
    StatisticReconciliationActualRelationalProofFacts Facts,
    string FirstRelationalResolutionSha256,
    string SecondRelationalResolutionSha256,
    string DoubleResolveProofSha256,
    string SemanticSha256)
{
    internal static StatisticReconciliationActualRelationalProofBinding Normalize(
        StatisticReconciliationActualRelationalProofBinding value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var facts = StatisticReconciliationActualRelationalProofOwner
            .NormalizeFacts(value.Facts);
        var first = Sha(value.FirstRelationalResolutionSha256,
            "RELATIONAL_FIRST_RESOLUTION_SHA256");
        var second = Sha(value.SecondRelationalResolutionSha256,
            "RELATIONAL_SECOND_RESOLUTION_SHA256");
        if (first != second || first != facts.SemanticSha256)
            throw Invalid("RELATIONAL_RESOLUTION_FACTS_MISMATCH");
        var doubleResolve = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_RELATIONAL_DOUBLE_RESOLVE_V3", first, second);
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_RELATIONAL_PROOF_BINDING_V3",
            StatisticReconciliationActualRelationalProofSchemas.Binding,
            facts.SemanticSha256, first, second, doubleResolve);
        if (value.SchemaVersion !=
                StatisticReconciliationActualRelationalProofSchemas.Binding ||
            Sha(value.DoubleResolveProofSha256,
                "RELATIONAL_DOUBLE_RESOLVE_SHA256") != doubleResolve ||
            Sha(value.SemanticSha256, "RELATIONAL_BINDING_SHA256") != semantic)
            throw Invalid("RELATIONAL_BINDING_SEMANTIC_MISMATCH");
        return new(
            StatisticReconciliationActualRelationalProofSchemas.Binding,
            facts, first, second, doubleResolve, semantic);
    }

    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);
    private static StatisticReconciliationActualObservationException Invalid(
        string reason) => new($"RELATIONAL_PROOF_BINDING:{reason}");
}

internal sealed record StatisticReconciliationActualRelationalProofResolution(
    string SchemaVersion,
    string State,
    string FailureCode,
    ImmutableArray<string> RequiredPersistenceFields,
    StatisticReconciliationActualRelationalProofBinding? Binding,
    string ResolutionSha256);

internal interface IStatisticReconciliationActualSummaryOwnerParityProver
{
    StatisticReconciliationActualSummaryOwnerParityProof Prove(
        StatisticReconciliationActualSummaryOwnerParityPlan plan,
        StatisticReconciliationActualSummaryOwnerParityActual actual);
}

internal sealed class StatisticReconciliationActualSummaryOwnerParityProver
    : IStatisticReconciliationActualSummaryOwnerParityProver
{
    public StatisticReconciliationActualSummaryOwnerParityProof Prove(
        StatisticReconciliationActualSummaryOwnerParityPlan plan,
        StatisticReconciliationActualSummaryOwnerParityActual actual)
        => StatisticReconciliationActualSummaryOwnerParity.Prove(plan, actual);
}

internal interface IStatisticReconciliationActualRelationalProofOwner
{
    Task<StatisticReconciliationActualRelationalProofResolution> ResolveAsync(
        StatisticReconciliationActualRelationalProofCommand command,
        CancellationToken cancellationToken = default);
}

internal sealed class StatisticReconciliationActualRelationalProofOwner(
    IStatisticReconciliationActualRawSummaryOwner rawOwner,
    IStatisticReconciliationActualSummaryOwnerParityProver summaryParity,
    IStatisticReconciliationActualExtendedRawSourceOwner extendedRawOwner,
    IStatisticReconciliationActualExtendedRawSourceOwnerParity extendedOwnerParity,
    IStatisticReconciliationActualCrossViewV3Owner crossViewOwner)
    : IStatisticReconciliationActualRelationalProofOwner
{
    public async Task<StatisticReconciliationActualRelationalProofResolution>
        ResolveAsync(
            StatisticReconciliationActualRelationalProofCommand command,
            CancellationToken cancellationToken = default)
    {
        if (command is null || command.SummaryPlan is null ||
            command.Material is null || command.Source is null ||
            command.Direct is null || command.Aggregate is null ||
            command.ExtendedRawSource is null ||
            command.ExtendedOwnerParity is null || command.Export is null)
            return Incomplete("RELATIONAL_PROOF_INPUT_REQUIRED", []);
        if (command.SchemaVersion !=
            StatisticReconciliationActualRelationalProofSchemas.Command)
            return Incomplete("RELATIONAL_PROOF_SCHEMA_UNSUPPORTED", []);

        try
        {
            var plan = StatisticReconciliationActualSummaryPlanBinding.Normalize(
                command.SummaryPlan);
            RequireSummaryCaptureShape(command);
            var first = await ResolveSingleAsync(command with
                {
                    SummaryPlan = plan
                }, cancellationToken).ConfigureAwait(false);
            if (!first.Complete)
                return Incomplete(first.FailureCode,
                    first.RequiredPersistenceFields);
            var second = await ResolveSingleAsync(command with
                {
                    SummaryPlan = plan
                }, cancellationToken).ConfigureAwait(false);
            if (!second.Complete)
                return Incomplete(second.FailureCode,
                    second.RequiredPersistenceFields);
            if (first.Facts != second.Facts)
                return Incomplete("RELATIONAL_DOUBLE_RESOLVE_DRIFT", []);
            var binding = BindDoubleResolved(
                first.Facts!, first.ResolutionSha256,
                second.Facts!, second.ResolutionSha256);
            return Complete(binding);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is
            StatisticReconciliationActualObservationException or
            ArgumentException or InvalidOperationException or JsonException or
            OverflowException or FormatException or NullReferenceException or
            KeyNotFoundException or IndexOutOfRangeException or
            InvalidCastException)
        {
            return Incomplete("RELATIONAL_PROOF_OWNER_INVALID", []);
        }
    }

    private async Task<SingleResolution> ResolveSingleAsync(
        StatisticReconciliationActualRelationalProofCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var central = await ResolveCentralParityAsync(
                command, cancellationToken)
            .ConfigureAwait(false);

        StatisticReconciliationActualExtendedRawSourceIntegrity
            .RequireResolution(command.ExtendedRawSource);
        var extendedParity = extendedOwnerParity.Prove(new(
            command.SummaryPlan, command.ExtendedRawSource,
            command.Advanced, command.Diff));
        RequireExtendedParity(extendedParity, command);
        RequireExtendedParity(command.ExtendedOwnerParity, command);
        if (!StringComparer.Ordinal.Equals(
                extendedParity.ProofSha256,
                command.ExtendedOwnerParity.ProofSha256))
            return SingleIncomplete(
                "EXTENDED_OWNER_PARITY_REPLAY_DRIFT", []);

        var extendedReplay = await extendedRawOwner.ResolveAsync(new(
                StatisticReconciliationActualExtendedRawSourceSchemas.Command,
                command.Material, command.SummaryPlan, command.Advanced,
                command.Diff), cancellationToken)
            .ConfigureAwait(false);
        StatisticReconciliationActualExtendedRawSourceIntegrity
            .RequireResolution(extendedReplay);
        if (!StringComparer.Ordinal.Equals(
                extendedReplay.ProofSha256,
                command.ExtendedRawSource.ProofSha256))
            return SingleIncomplete(
                "EXTENDED_RAW_SOURCE_REPLAY_DRIFT", []);

        var cross = await crossViewOwner.ResolveAsync(
                new StatisticReconciliationActualCrossViewV3OwnerCommand(
                    StatisticReconciliationActualCrossViewV3OwnerSchemas.Command,
                    command.Material,
                    command.Source,
                    command.Direct,
                    command.Aggregate,
                    command.Basic,
                    command.Advanced,
                    command.Diff,
                    command.Api,
                    command.Export),
                cancellationToken)
            .ConfigureAwait(false);
        if (cross is null || cross.State !=
                StatisticReconciliationActualCrossViewV2OwnerStates.Complete ||
            cross.FailureCode !=
                StatisticReconciliationActualCrossViewV2OwnerFailures.None ||
            cross.Authorization is null || cross.Proof is null ||
            cross.Plan is null || cross.Base is null ||
            cross.Actual is null || !cross.Proof.Complete)
            return SingleIncomplete(
                $"CROSS_VIEW_INCOMPLETE:{cross?.FailureCode ?? "NULL"}",
                cross?.RequiredPersistenceFields ?? []);
        RequireCrossView(cross, command);

        var facts = CreateFacts(command, central, extendedReplay,
            extendedParity, cross);
        return new(true, "NONE", [], facts, facts.SemanticSha256);
    }
    private static void RequireSummaryCaptureShape(
        StatisticReconciliationActualRelationalProofCommand command)
    {
        var plan = command.Material.Run.ActualCapturePlan ??
            throw Invalid("RELATIONAL_ACTUAL_CAPTURE_PLAN_REQUIRED");
        StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
            plan, plan.PlanSha256);
        if (!StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan))
        {
            if (command.Basic is null || command.Advanced is null ||
                command.Diff is null || command.Material.Basic is null ||
                command.Material.Advanced is null ||
                command.Material.Diff is null)
                throw Invalid("RELATIONAL_SUMMARY_CAPTURE_REQUIRED");
            return;
        }

        var basicNotApplicable =
            StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                plan.Basic.Disposition);
        var advancedNotApplicable =
            StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                plan.Advanced.Disposition);
        var diffNotApplicable =
            StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                plan.Diff.Disposition);
        if ((command.Basic is null) != basicNotApplicable ||
            (command.Advanced is null) != advancedNotApplicable ||
            (command.Diff is null) != diffNotApplicable ||
            (command.Material.Basic is null) != basicNotApplicable ||
            (command.Material.Advanced is null) != advancedNotApplicable ||
            (command.Material.Diff is null) != diffNotApplicable)
            throw Invalid("RELATIONAL_SUMMARY_APPLICABILITY_MISMATCH");
    }

    private static string SummaryCaptureSha(
        StatisticReconciliationActualRelationalProofCommand command,
        string family)
    {
        var captured = family switch
        {
            StatisticReconciliationActualCapturePlanIntegrity.BasicFamily =>
                command.Basic?.CaptureSemanticSha256,
            StatisticReconciliationActualCapturePlanIntegrity.AdvancedFamily =>
                command.Advanced?.CaptureSemanticSha256,
            StatisticReconciliationActualCapturePlanIntegrity.DiffFamily =>
                command.Diff?.CaptureSemanticSha256,
            _ => throw Invalid("RELATIONAL_SUMMARY_FAMILY_INVALID")
        };
        if (captured is not null)
            return captured;
        return StatisticReconciliationActualCapturePlanIntegrity
            .NotApplicableCaptureSha(
                command.Material.Run.ActualCapturePlan ??
                throw Invalid("RELATIONAL_ACTUAL_CAPTURE_PLAN_REQUIRED"),
                family);
    }
    private static StatisticReconciliationActualRelationalProofFacts CreateFacts(
        StatisticReconciliationActualRelationalProofCommand command,
        CentralParityResolution central,
        StatisticReconciliationActualExtendedRawSourceResolution extended,
        StatisticReconciliationActualExtendedRawSourceOwnerParityProof
            extendedParity,
        StatisticReconciliationActualCrossViewV3OwnerResolution cross)
    {
        var advancedContributionManifest =
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_RELATIONAL_EXTENDED_ADVANCED_CONTRIBUTIONS_V1",
                extended.Advanced!.Grains.Select(value =>
                    value.DescriptorContributionManifestSha256));
        var advancedContributionCount = checked(
            extended.Advanced.Grains.Sum(value =>
                value.DescriptorContributionCount));
        var extendedAdvancedProjectionManifest =
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_RELATIONAL_EXTENDED_ADVANCED_DESCRIPTOR_PROJECTIONS_V1",
                extended.Advanced.Grains.Select(value =>
                    value.DescriptorProjectionManifestSha256));
        var extendedAdvancedProjectionCount = checked(
            extended.Advanced.Grains.Sum(value =>
                value.DescriptorProjectionCount));
        var ownerDescriptorManifest =
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_RELATIONAL_EXTENDED_OWNER_DESCRIPTORS_V1",
                extendedParity.Families.Select(value =>
                    value.DescriptorManifestSha256));
        var ownerDescriptorCount = checked(
            extendedParity.Families.Sum(value => value.DescriptorCount));
        var proof = cross.Proof!;
        var authorization = cross.Authorization!;
        var draft = new StatisticReconciliationActualRelationalProofFacts(
            StatisticReconciliationActualRelationalProofSchemas.Facts,
            command.SummaryPlan.SemanticSha256,
            central.ProjectedPlanSha256,
            central.PlanProjectionProofSha256,
            command.Source.CaptureSemanticSha256,
            command.Direct.CaptureSemanticSha256,
            command.Aggregate.CaptureSemanticSha256,
            SummaryCaptureSha(command,
                StatisticReconciliationActualCapturePlanIntegrity.BasicFamily),
            SummaryCaptureSha(command,
                StatisticReconciliationActualCapturePlanIntegrity.AdvancedFamily),
            SummaryCaptureSha(command,
                StatisticReconciliationActualCapturePlanIntegrity.DiffFamily),
            command.Api?.CaptureSemanticSha256,
            command.Export.CaptureSemanticSha256,
            central.RawProofSha256,
            central.RawSourceManifestSha256,
            central.RawSourceCount,
            central.RawAtomManifestSha256,
            central.RawAtomCount,
            central.RawDoubleCollectProofSha256,
            central.ParityProofSha256,
            central.ParityRawManifestSha256,
            central.ParityRawAtomCount,
            central.ParityOwnerManifestSha256,
            central.ParityOwnerItemCount,
            central.ParityRelationManifestSha256,
            central.ParityRelationCount,
            central.DirectParityProofSha256,
            central.DirectParityOwnerManifestSha256,
            central.DirectParityOwnerItemCount,
            central.DirectParityRelationManifestSha256,
            central.DirectParityRelationCount,
            extended.ProofSha256,
            extended.Advanced!.ProofSha256,
            extended.Advanced.SchemaOptionBindingSha256,
            extendedAdvancedProjectionManifest,
            extendedAdvancedProjectionCount,
            extended.Advanced.TypedAtomManifestSha256,
            extended.Advanced.TypedAtomCount,
            advancedContributionManifest,
            advancedContributionCount,
            extended.Diff!.ProofSha256,
            extended.Diff.PeriodBindingSha256,
            extended.Diff.TypedAtomManifestSha256,
            extended.Diff.TypedAtomCount,
            extended.Diff.SourcePairManifestSha256,
            extended.Diff.SourcePairCount,
            extended.PartitionDoubleCollectManifestSha256,
            extended.PartitionDoubleCollectCount,
            extendedParity.ProofSha256,
            ownerDescriptorManifest,
            ownerDescriptorCount,
            extendedParity.TypedAtomManifestSha256,
            extendedParity.TypedAtomCount,
            extendedParity.OwnerManifestSha256,
            extendedParity.OwnerItemCount,
            extendedParity.RelationManifestSha256,
            extendedParity.RelationCount,
            authorization.SemanticSha256,
            cross.ResolutionSha256,
            proof.ProofSha256,
            proof.CompatibilityProofSha256,
            proof.OriginalApiSemanticSha256,
            proof.OriginalExportSemanticSha256,
            "~");
        return NormalizeFacts(draft with
        {
            SemanticSha256 = ComputeFactsSemantic(draft)
        });
    }

    internal static StatisticReconciliationActualRelationalProofFacts
        NormalizeFacts(StatisticReconciliationActualRelationalProofFacts value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.SchemaVersion !=
                StatisticReconciliationActualRelationalProofSchemas.Facts ||
            value.RawSourceCount < 0 || value.RawAtomCount < 0 ||
            value.SummaryParityRawAtomCount < 0 ||
            value.SummaryParityRawAtomCount != value.RawAtomCount ||
            value.SummaryParityOwnerItemCount < 0 ||
            value.SummaryParityRelationCount < 0 ||
            value.DirectParityOwnerItemCount < 0 ||
            value.DirectParityRelationCount < 0 ||
            value.ExtendedAdvancedDescriptorProjectionCount < 0 ||
            value.ExtendedAdvancedTypedAtomCount < 0 ||
            value.ExtendedAdvancedDescriptorContributionCount < 0 ||
            value.ExtendedDiffTypedAtomCount < 0 ||
            value.ExtendedDiffSourcePairCount < 0 ||
            value.ExtendedPartitionDoubleCollectCount < 0 ||
            value.ExtendedOwnerDescriptorCount < 0 ||
            value.ExtendedOwnerTypedAtomCount < 0 ||
            value.ExtendedOwnerTypedAtomCount != checked(
                value.ExtendedAdvancedTypedAtomCount +
                value.ExtendedDiffTypedAtomCount) ||
            value.ExtendedOwnerItemCount < 0 ||
            value.ExtendedOwnerRelationCount < 0)
            throw Invalid("RELATIONAL_FACT_COUNTS_INVALID");
        var normalized = value with
        {
            SummaryPlanBindingSha256 = Sha(value.SummaryPlanBindingSha256,
                "RELATIONAL_PLAN_SHA256"),
            CentralProjectedPlanSha256 = Sha(
                value.CentralProjectedPlanSha256,
                "RELATIONAL_CENTRAL_PROJECTED_PLAN_SHA256"),
            CentralPlanProjectionProofSha256 = Sha(
                value.CentralPlanProjectionProofSha256,
                "RELATIONAL_CENTRAL_PLAN_PROJECTION_PROOF_SHA256"),
            SourceCaptureSha256 = Sha(value.SourceCaptureSha256,
                "RELATIONAL_SOURCE_CAPTURE_SHA256"),
            DirectCaptureSha256 = Sha(value.DirectCaptureSha256,
                "RELATIONAL_DIRECT_CAPTURE_SHA256"),
            AggregateCaptureSha256 = Sha(value.AggregateCaptureSha256,
                "RELATIONAL_AGGREGATE_CAPTURE_SHA256"),
            BasicCaptureSha256 = Sha(value.BasicCaptureSha256,
                "RELATIONAL_BASIC_CAPTURE_SHA256"),
            AdvancedCaptureSha256 = Sha(value.AdvancedCaptureSha256,
                "RELATIONAL_ADVANCED_CAPTURE_SHA256"),
            DiffCaptureSha256 = Sha(value.DiffCaptureSha256,
                "RELATIONAL_DIFF_CAPTURE_SHA256"),
            ApiCaptureSha256 = OptionalSha(value.ApiCaptureSha256,
                "RELATIONAL_API_CAPTURE_SHA256"),
            ExportCaptureSha256 = Sha(value.ExportCaptureSha256,
                "RELATIONAL_EXPORT_CAPTURE_SHA256"),
            RawProofSha256 = Sha(value.RawProofSha256,
                "RELATIONAL_RAW_PROOF_SHA256"),
            RawSourceManifestSha256 = Sha(value.RawSourceManifestSha256,
                "RELATIONAL_RAW_SOURCE_MANIFEST_SHA256"),
            RawAtomManifestSha256 = Sha(value.RawAtomManifestSha256,
                "RELATIONAL_RAW_ATOM_MANIFEST_SHA256"),
            RawDoubleCollectProofSha256 = Sha(
                value.RawDoubleCollectProofSha256,
                "RELATIONAL_RAW_DOUBLE_COLLECT_SHA256"),
            SummaryParityProofSha256 = Sha(value.SummaryParityProofSha256,
                "RELATIONAL_SUMMARY_PROOF_SHA256"),
            SummaryParityRawManifestSha256 = Sha(
                value.SummaryParityRawManifestSha256,
                "RELATIONAL_SUMMARY_RAW_MANIFEST_SHA256"),
            SummaryParityOwnerManifestSha256 = Sha(
                value.SummaryParityOwnerManifestSha256,
                "RELATIONAL_SUMMARY_OWNER_MANIFEST_SHA256"),
            SummaryParityRelationManifestSha256 = Sha(
                value.SummaryParityRelationManifestSha256,
                "RELATIONAL_SUMMARY_RELATION_MANIFEST_SHA256"),
            DirectParityProofSha256 = Sha(value.DirectParityProofSha256,
                "RELATIONAL_DIRECT_PARITY_PROOF_SHA256"),
            DirectParityOwnerManifestSha256 = Sha(
                value.DirectParityOwnerManifestSha256,
                "RELATIONAL_DIRECT_PARITY_OWNER_MANIFEST_SHA256"),
            DirectParityRelationManifestSha256 = Sha(
                value.DirectParityRelationManifestSha256,
                "RELATIONAL_DIRECT_PARITY_RELATION_MANIFEST_SHA256"),
            ExtendedRawResolutionProofSha256 = Sha(
                value.ExtendedRawResolutionProofSha256,
                "RELATIONAL_EXTENDED_RESOLUTION_PROOF_SHA256"),
            ExtendedAdvancedProofSha256 = Sha(
                value.ExtendedAdvancedProofSha256,
                "RELATIONAL_EXTENDED_ADVANCED_PROOF_SHA256"),
            ExtendedAdvancedSchemaOptionBindingSha256 = Sha(
                value.ExtendedAdvancedSchemaOptionBindingSha256,
                "RELATIONAL_EXTENDED_ADVANCED_SCHEMA_OPTION_BINDING_SHA256"),
            ExtendedAdvancedDescriptorProjectionManifestSha256 = Sha(
                value.ExtendedAdvancedDescriptorProjectionManifestSha256,
                "RELATIONAL_EXTENDED_ADVANCED_DESCRIPTOR_PROJECTION_MANIFEST_SHA256"),
            ExtendedAdvancedTypedAtomManifestSha256 = Sha(
                value.ExtendedAdvancedTypedAtomManifestSha256,
                "RELATIONAL_EXTENDED_ADVANCED_ATOM_MANIFEST_SHA256"),
            ExtendedAdvancedDescriptorContributionManifestSha256 = Sha(
                value.ExtendedAdvancedDescriptorContributionManifestSha256,
                "RELATIONAL_EXTENDED_ADVANCED_CONTRIBUTION_MANIFEST_SHA256"),
            ExtendedDiffProofSha256 = Sha(
                value.ExtendedDiffProofSha256,
                "RELATIONAL_EXTENDED_DIFF_PROOF_SHA256"),
            ExtendedDiffPeriodBindingSha256 = Sha(
                value.ExtendedDiffPeriodBindingSha256,
                "RELATIONAL_EXTENDED_DIFF_PERIOD_BINDING_SHA256"),
            ExtendedDiffTypedAtomManifestSha256 = Sha(
                value.ExtendedDiffTypedAtomManifestSha256,
                "RELATIONAL_EXTENDED_DIFF_ATOM_MANIFEST_SHA256"),
            ExtendedDiffSourcePairManifestSha256 = Sha(
                value.ExtendedDiffSourcePairManifestSha256,
                "RELATIONAL_EXTENDED_DIFF_PAIR_MANIFEST_SHA256"),
            ExtendedPartitionDoubleCollectManifestSha256 = Sha(
                value.ExtendedPartitionDoubleCollectManifestSha256,
                "RELATIONAL_EXTENDED_PARTITION_MANIFEST_SHA256"),
            ExtendedOwnerParityProofSha256 = Sha(
                value.ExtendedOwnerParityProofSha256,
                "RELATIONAL_EXTENDED_OWNER_PARITY_SHA256"),
            ExtendedOwnerDescriptorManifestSha256 = Sha(
                value.ExtendedOwnerDescriptorManifestSha256,
                "RELATIONAL_EXTENDED_OWNER_DESCRIPTOR_MANIFEST_SHA256"),
            ExtendedOwnerTypedAtomManifestSha256 = Sha(
                value.ExtendedOwnerTypedAtomManifestSha256,
                "RELATIONAL_EXTENDED_OWNER_TYPED_MANIFEST_SHA256"),
            ExtendedOwnerItemManifestSha256 = Sha(
                value.ExtendedOwnerItemManifestSha256,
                "RELATIONAL_EXTENDED_OWNER_ITEM_MANIFEST_SHA256"),
            ExtendedOwnerRelationManifestSha256 = Sha(
                value.ExtendedOwnerRelationManifestSha256,
                "RELATIONAL_EXTENDED_OWNER_RELATION_MANIFEST_SHA256"),
            CrossViewAuthorizationRelationSha256 = Sha(
                value.CrossViewAuthorizationRelationSha256,
                "RELATIONAL_XVIEW_AUTH_RELATION_SHA256"),
            CrossViewResolutionSha256 = Sha(value.CrossViewResolutionSha256,
                "RELATIONAL_XVIEW_RESOLUTION_SHA256"),
            CrossViewProofSha256 = Sha(value.CrossViewProofSha256,
                "RELATIONAL_XVIEW_PROOF_SHA256"),
            CrossViewCompatibilityProofSha256 = Sha(
                value.CrossViewCompatibilityProofSha256,
                "RELATIONAL_XVIEW_COMPATIBILITY_SHA256"),
            CrossViewOriginalApiSemanticSha256 = OriginalApi(
                value.CrossViewOriginalApiSemanticSha256,
                value.ApiCaptureSha256),
            CrossViewOriginalExportSemanticSha256 = Sha(
                value.CrossViewOriginalExportSemanticSha256,
                "RELATIONAL_XVIEW_EXPORT_SEMANTIC_SHA256"),
            SemanticSha256 = Sha(value.SemanticSha256,
                "RELATIONAL_FACTS_SHA256")
        };
        var crossViewProof = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CROSS_VIEW_PARITY_PROOF_V3",
            StatisticReconciliationActualCrossViewParityV3Schemas.Proof,
            normalized.CrossViewAuthorizationRelationSha256,
            normalized.CrossViewCompatibilityProofSha256,
            normalized.CrossViewOriginalApiSemanticSha256,
            normalized.CrossViewOriginalExportSemanticSha256);
        var crossViewResolution = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CROSS_VIEW_V3_OWNER_RESOLUTION_V1",
            StatisticReconciliationActualCrossViewV3OwnerSchemas.Resolution,
            StatisticReconciliationActualCrossViewV2OwnerStates.Complete,
            normalized.CrossViewAuthorizationRelationSha256,
            crossViewProof);
        var semantic = ComputeFactsSemantic(normalized);
        if (crossViewProof != normalized.CrossViewProofSha256 ||
            crossViewResolution != normalized.CrossViewResolutionSha256 ||
            semantic != normalized.SemanticSha256 ||
            normalized.CrossViewOriginalApiSemanticSha256 !=
                (normalized.ApiCaptureSha256 ?? "API_NOT_APPLICABLE") ||
            normalized.CrossViewOriginalExportSemanticSha256 !=
                normalized.ExportCaptureSha256)
            throw Invalid("RELATIONAL_FACTS_SEMANTIC_MISMATCH");
        return normalized;
    }

    internal static StatisticReconciliationActualRelationalProofBinding
        BindDoubleResolved(
            StatisticReconciliationActualRelationalProofFacts first,
            string firstSha,
            StatisticReconciliationActualRelationalProofFacts second,
            string secondSha)
    {
        first = NormalizeFacts(first);
        second = NormalizeFacts(second);
        firstSha = Sha(firstSha, "RELATIONAL_FIRST_RESOLUTION_SHA256");
        secondSha = Sha(secondSha, "RELATIONAL_SECOND_RESOLUTION_SHA256");
        if (first != second || firstSha != first.SemanticSha256 ||
            secondSha != second.SemanticSha256)
            throw Invalid("RELATIONAL_DOUBLE_RESOLVE_FACT_MISMATCH");
        var doubleResolve = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_RELATIONAL_DOUBLE_RESOLVE_V3", firstSha, secondSha);
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_RELATIONAL_PROOF_BINDING_V3",
            StatisticReconciliationActualRelationalProofSchemas.Binding,
            first.SemanticSha256, firstSha, secondSha, doubleResolve);
        return StatisticReconciliationActualRelationalProofBinding.Normalize(new(
            StatisticReconciliationActualRelationalProofSchemas.Binding,
            first, firstSha, secondSha, doubleResolve, semantic));
    }

    internal static string ComputeFactsSemantic(
        StatisticReconciliationActualRelationalProofFacts value)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_RELATIONAL_PROOF_FACTS_V3",
            StatisticReconciliationActualRelationalProofSchemas.Facts,
            value.SummaryPlanBindingSha256,
            value.CentralProjectedPlanSha256,
            value.CentralPlanProjectionProofSha256,
            value.SourceCaptureSha256,
            value.DirectCaptureSha256,
            value.AggregateCaptureSha256,
            value.BasicCaptureSha256,
            value.AdvancedCaptureSha256,
            value.DiffCaptureSha256,
            value.ApiCaptureSha256 ?? "API_NOT_APPLICABLE",
            value.ExportCaptureSha256,
            value.RawProofSha256,
            value.RawSourceManifestSha256,
            I(value.RawSourceCount),
            value.RawAtomManifestSha256,
            I(value.RawAtomCount),
            value.RawDoubleCollectProofSha256,
            value.SummaryParityProofSha256,
            value.SummaryParityRawManifestSha256,
            I(value.SummaryParityRawAtomCount),
            value.SummaryParityOwnerManifestSha256,
            I(value.SummaryParityOwnerItemCount),
            value.SummaryParityRelationManifestSha256,
            I(value.SummaryParityRelationCount),
            value.DirectParityProofSha256,
            value.DirectParityOwnerManifestSha256,
            I(value.DirectParityOwnerItemCount),
            value.DirectParityRelationManifestSha256,
            I(value.DirectParityRelationCount),
            value.ExtendedRawResolutionProofSha256,
            value.ExtendedAdvancedProofSha256,
            value.ExtendedAdvancedSchemaOptionBindingSha256,
            value.ExtendedAdvancedDescriptorProjectionManifestSha256,
            I(value.ExtendedAdvancedDescriptorProjectionCount),
            value.ExtendedAdvancedTypedAtomManifestSha256,
            I(value.ExtendedAdvancedTypedAtomCount),
            value.ExtendedAdvancedDescriptorContributionManifestSha256,
            I(value.ExtendedAdvancedDescriptorContributionCount),
            value.ExtendedDiffProofSha256,
            value.ExtendedDiffPeriodBindingSha256,
            value.ExtendedDiffTypedAtomManifestSha256,
            I(value.ExtendedDiffTypedAtomCount),
            value.ExtendedDiffSourcePairManifestSha256,
            I(value.ExtendedDiffSourcePairCount),
            value.ExtendedPartitionDoubleCollectManifestSha256,
            I(value.ExtendedPartitionDoubleCollectCount),
            value.ExtendedOwnerParityProofSha256,
            value.ExtendedOwnerDescriptorManifestSha256,
            I(value.ExtendedOwnerDescriptorCount),
            value.ExtendedOwnerTypedAtomManifestSha256,
            I(value.ExtendedOwnerTypedAtomCount),
            value.ExtendedOwnerItemManifestSha256,
            I(value.ExtendedOwnerItemCount),
            value.ExtendedOwnerRelationManifestSha256,
            I(value.ExtendedOwnerRelationCount),
            value.CrossViewAuthorizationRelationSha256,
            value.CrossViewResolutionSha256,
            value.CrossViewProofSha256,
            value.CrossViewCompatibilityProofSha256,
            value.CrossViewOriginalApiSemanticSha256,
            value.CrossViewOriginalExportSemanticSha256);

    private async Task<CentralParityResolution> ResolveCentralParityAsync(
        StatisticReconciliationActualRelationalProofCommand command,
        CancellationToken cancellationToken)
    {
        var projection = ProjectCentralPlan(command.SummaryPlan);
        if (projection.Plan is null)
        {
            var membership = Sha(command.Source.MembershipSemanticSha256,
                "RELATIONAL_CENTRAL_MEMBERSHIP_SHA256");
            var sourceManifest = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_CENTRAL_RAW_SOURCE_NOT_APPLICABLE_V1",
                projection.ProjectionProofSha256,
                command.Source.CaptureSemanticSha256,
                membership);
            var atomManifest =
                StatisticReconciliationActualCanonical.HashSequence(
                    "P10_ACTUAL_CENTRAL_RAW_ATOMS_NOT_APPLICABLE_V1", []);
            var doubleCollect = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_CENTRAL_DOUBLE_COLLECT_NOT_APPLICABLE_V1",
                projection.ProjectionProofSha256,
                command.Source.CaptureSemanticSha256,
                membership);
            var rawProof = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_CENTRAL_RAW_PROOF_NOT_APPLICABLE_V1",
                projection.ProjectedPlanSha256,
                projection.ProjectionProofSha256,
                command.Source.CaptureSemanticSha256,
                membership, sourceManifest, I(0), atomManifest, I(0),
                doubleCollect);
            var parityRaw =
                StatisticReconciliationActualCanonical.HashSequence(
                    "P10_ACTUAL_CENTRAL_PARITY_RAW_NOT_APPLICABLE_V1", []);
            var parityOwner =
                StatisticReconciliationActualCanonical.HashSequence(
                    "P10_ACTUAL_CENTRAL_PARITY_OWNER_NOT_APPLICABLE_V1", []);
            var parityRelation =
                StatisticReconciliationActualCanonical.HashSequence(
                    "P10_ACTUAL_CENTRAL_PARITY_RELATION_NOT_APPLICABLE_V1", []);
            var parityProof = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_CENTRAL_OWNER_PARITY_NOT_APPLICABLE_V1",
                projection.ProjectedPlanSha256,
                projection.ProjectionProofSha256,
                rawProof, parityRaw, I(0), parityOwner, I(0),
                parityRelation, I(0));
            var directOwner = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_DIRECT_OWNER_NOT_APPLICABLE_V1",
                projection.ProjectedPlanSha256,
                command.Direct.CaptureSemanticSha256);
            var directRelation =
                StatisticReconciliationActualCanonical.HashSequence(
                    "P10_ACTUAL_SUMMARY_OWNER_DIRECT_RELATIONS_V1", []);
            var directProof = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_DIRECT_PARITY_NOT_APPLICABLE_V1",
                projection.ProjectedPlanSha256, rawProof,
                command.Direct.CaptureSemanticSha256, directOwner,
                directRelation);
            return new(projection.ProjectedPlanSha256,
                projection.ProjectionProofSha256, rawProof, sourceManifest, 0,
                atomManifest, 0, doubleCollect, parityProof, parityRaw, 0,
                parityOwner, 0, parityRelation, 0, directProof, directOwner, 0,
                directRelation, 0);
        }

        var raw = await rawOwner.ResolveAsync(
                projection.Plan, command.Source, cancellationToken,
                projectDynamicFormDirectFields:
                    StatisticReconciliationActualCapturePlanIntegrity.IsV4(
                        command.Material.Run.ActualCapturePlan))
            .ConfigureAwait(false);
        if (raw is null || raw.SummaryPlanBindingSha256 !=
                projection.Plan.SemanticSha256 ||
            raw.ActualSourceCaptureSha256 !=
                command.Source.CaptureSemanticSha256 ||
            raw.ActualMembershipSemanticSha256 !=
                command.Source.MembershipSemanticSha256)
            throw Invalid(
                "RELATIONAL_CENTRAL_RAW_SUMMARY_BINDING_INVALID");

        var directParity =
            StatisticReconciliationActualDirectRawOwnerParity.Prove(
                projection.Plan, raw, command.Source, command.Direct);
        RequireDirectParity(directParity, projection.Plan, raw,
            command.Direct);

        var parity = summaryParity.Prove(
            new StatisticReconciliationActualSummaryOwnerParityPlan(
                projection.Plan, raw),
            new StatisticReconciliationActualSummaryOwnerParityActual(
                command.Basic, null, null));
        if (parity is null || !parity.Complete ||
            parity.FailureCode is not null ||
            parity.SummaryPlanBindingSha256 !=
                projection.Plan.SemanticSha256 ||
            parity.RawProofSha256 != raw.ProofSha256)
            throw Invalid("RELATIONAL_CENTRAL_PARITY_INCOMPLETE");
        RequireCentralSummaryParity(parity);
        return new(projection.ProjectedPlanSha256,
            projection.ProjectionProofSha256,
            raw.ProofSha256, raw.SourceManifestSha256, raw.Sources.Length,
            raw.AtomManifestSha256, raw.Atoms.Length,
            raw.DoubleCollectProofSha256,
            parity.ProofSha256, parity.RawManifestSha256,
            parity.RawAtomCount, parity.OwnerManifestSha256,
            parity.OwnerItemCount, parity.RelationManifestSha256,
            parity.RelationCount, directParity.ProofSha256,
            directParity.OwnerManifestSha256, directParity.OwnerItemCount,
            directParity.RelationManifestSha256,
            directParity.RelationCount);
    }

    internal static void RequirePlanProjections(
        StatisticReconciliationActualRelationalProofFacts facts,
        StatisticReconciliationActualSummaryPlanBinding fullPlan)
    {
        facts = NormalizeFacts(facts);
        fullPlan = StatisticReconciliationActualSummaryPlanBinding.Normalize(
            fullPlan);
        var projection = ProjectCentralPlan(fullPlan);
        if (facts.CentralProjectedPlanSha256 !=
                projection.ProjectedPlanSha256 ||
            facts.CentralPlanProjectionProofSha256 !=
                projection.ProjectionProofSha256)
            throw Invalid("RELATIONAL_CENTRAL_PLAN_PROJECTION_MISMATCH");
        var directDescriptorCount = fullPlan.IdentityDescriptors.Count(value =>
            value.Family == "DIRECT");
        if (facts.DirectParityRelationCount != directDescriptorCount)
            throw Invalid("RELATIONAL_DIRECT_PLAN_PROJECTION_MISMATCH");
        var basicDescriptorCount = fullPlan.IdentityDescriptors.Count(value =>
            value.Family == "BASIC");
        if (facts.SummaryParityRelationCount != basicDescriptorCount)
            throw Invalid("RELATIONAL_BASIC_PLAN_PROJECTION_MISMATCH");
        var extendedFamilies = new[] { "ADVANCED", "DIFF" };
        var familyDescriptorManifests = extendedFamilies.Select(family =>
            StatisticReconciliationActualExtendedRawSourceTypedCompiler
                .DescriptorManifest(
                    $"P10_ACTUAL_EXTENDED_OWNER_{family}_DESCRIPTORS_V1",
                    fullPlan.IdentityDescriptors.Where(value =>
                        value.Family == family).ToImmutableArray()));
        var descriptorManifest =
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_RELATIONAL_EXTENDED_OWNER_DESCRIPTORS_V1",
                familyDescriptorManifests);
        var advancedDescriptorCount = fullPlan.IdentityDescriptors.Count(
            value => value.Family == "ADVANCED");
        var diffDescriptorCount = fullPlan.IdentityDescriptors.Count(
            value => value.Family == "DIFF");
        var descriptorCount = checked(
            advancedDescriptorCount + diffDescriptorCount);
        var advancedApplicable = advancedDescriptorCount > 0;
        var diffApplicable = diffDescriptorCount > 0;
        var expectedAdvanced = advancedApplicable
            ? null
            : StatisticReconciliationActualExtendedRawSourceIntegrity
                .AdvancedNotApplicable(fullPlan.SemanticSha256);
        var expectedDiff = diffApplicable
            ? null
            : StatisticReconciliationActualExtendedRawSourceIntegrity
                .DiffNotApplicable(fullPlan.SemanticSha256);
        var emptyAdvancedProjectionManifest =
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_RELATIONAL_EXTENDED_ADVANCED_DESCRIPTOR_PROJECTIONS_V1",
                []);
        var emptyAdvancedContributionManifest =
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_RELATIONAL_EXTENDED_ADVANCED_CONTRIBUTIONS_V1",
                []);
        var emptyPartitionManifest =
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_EXTENDED_RAW_SOURCE_PARTITION_DOUBLE_COLLECT_MANIFEST_V2",
                []);
        if (facts.ExtendedOwnerDescriptorManifestSha256 !=
                descriptorManifest ||
            facts.ExtendedOwnerDescriptorCount != descriptorCount ||
            facts.ExtendedAdvancedDescriptorProjectionCount !=
                advancedDescriptorCount ||
            facts.ExtendedDiffSourcePairCount != diffDescriptorCount ||
            (!advancedApplicable &&
                (facts.ExtendedAdvancedProofSha256 !=
                    expectedAdvanced!.ProofSha256 ||
                 facts.ExtendedAdvancedSchemaOptionBindingSha256 !=
                    expectedAdvanced.SchemaOptionBindingSha256 ||
                 facts.ExtendedAdvancedDescriptorProjectionManifestSha256 !=
                    emptyAdvancedProjectionManifest ||
                 facts.ExtendedAdvancedTypedAtomManifestSha256 !=
                    expectedAdvanced.TypedAtomManifestSha256 ||
                 facts.ExtendedAdvancedTypedAtomCount != 0 ||
                 facts.ExtendedAdvancedDescriptorContributionManifestSha256 !=
                    emptyAdvancedContributionManifest ||
                 facts.ExtendedAdvancedDescriptorContributionCount != 0)) ||
            (advancedApplicable &&
                facts.ExtendedAdvancedDescriptorProjectionCount == 0) ||
            (!diffApplicable &&
                (facts.ExtendedDiffProofSha256 != expectedDiff!.ProofSha256 ||
                 facts.ExtendedDiffPeriodBindingSha256 !=
                    expectedDiff.PeriodBindingSha256 ||
                 facts.ExtendedDiffTypedAtomManifestSha256 !=
                    expectedDiff.TypedAtomManifestSha256 ||
                 facts.ExtendedDiffTypedAtomCount != 0 ||
                 facts.ExtendedDiffSourcePairManifestSha256 !=
                    expectedDiff.SourcePairManifestSha256)) ||
            (diffApplicable && facts.ExtendedDiffSourcePairCount == 0) ||
            (!advancedApplicable && !diffApplicable &&
                (facts.ExtendedPartitionDoubleCollectCount != 0 ||
                 facts.ExtendedPartitionDoubleCollectManifestSha256 !=
                    emptyPartitionManifest)) ||
            ((advancedApplicable || diffApplicable) &&
                facts.ExtendedPartitionDoubleCollectCount == 0))
            throw Invalid("RELATIONAL_EXTENDED_PLAN_PROJECTION_MISMATCH");
    }

    private static CentralPlanProjection ProjectCentralPlan(
        StatisticReconciliationActualSummaryPlanBinding fullPlan)
    {
        fullPlan = StatisticReconciliationActualSummaryPlanBinding.Normalize(
            fullPlan);
        var descriptors = fullPlan.IdentityDescriptors
            .Where(value => value.Family is "DIRECT" or "BASIC")
            .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray();
        var descriptorManifest =
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_CENTRAL_PLAN_DESCRIPTOR_MANIFEST_V1",
                descriptors.Select(value => value.SemanticSha256));
        StatisticReconciliationActualSummaryPlanBinding? projected = null;
        var projectedSha = descriptors.Length == 0
            ? StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_CENTRAL_PLAN_NOT_APPLICABLE_V1",
                fullPlan.SemanticSha256,
                fullPlan.ExpectedIdentitySetSha256,
                descriptorManifest, I(0))
            : (projected =
                StatisticReconciliationActualSummaryPlanBinding.Create(
                    new StatisticReconciliationExpectedGenerationBinding(
                        fullPlan.ExpectedReconciliationId,
                        fullPlan.ExpectedGenerationId,
                        fullPlan.ExpectedGenerationSha256,
                        fullPlan.ExpectedMetricPlanSha256,
                        descriptors.Length,
                        fullPlan.ExpectedManifestSha256,
                        fullPlan.ExpectedDocumentCount,
                        fullPlan.ExpectedMembershipSemanticSha256,
                        fullPlan.ExpectedRuntimeKind),
                    descriptors)).SemanticSha256;
        var projectionProof = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CENTRAL_PLAN_PROJECTION_V1",
            fullPlan.SemanticSha256,
            fullPlan.ExpectedIdentitySetSha256,
            I(fullPlan.MetricIdentityCount),
            "DIRECT", "BASIC",
            projectedSha,
            descriptorManifest,
            I(descriptors.Length));
        return new(projected, projectedSha, projectionProof);
    }
    private static void RequireDirectParity(
        StatisticReconciliationActualDirectRawOwnerParityProof proof,
        StatisticReconciliationActualSummaryPlanBinding plan,
        StatisticReconciliationActualRawSummaryProof raw,
        ActualDirectProjectionCapture direct)
    {
        var descriptorCount = plan.IdentityDescriptors.Count(value =>
            value.Family == "DIRECT");
        var applicable = descriptorCount > 0;
        var semantic = StatisticReconciliationActualCanonical.Hash(
            StatisticReconciliationActualDirectRawOwnerParity.SchemaVersion,
            StatisticReconciliationActualCanonical.Boolean(applicable),
            "COMPLETE",
            applicable ? "COMPLETE" : "DIRECT_NOT_APPLICABLE",
            plan.SemanticSha256, raw.ProofSha256,
            direct.CaptureSemanticSha256, proof.DescriptorManifestSha256,
            I(proof.DescriptorCount), proof.RawManifestSha256,
            I(proof.RawAtomCount), proof.OwnerManifestSha256,
            I(proof.OwnerItemCount), proof.RelationManifestSha256,
            I(proof.RelationCount));
        if (proof.SchemaVersion !=
                StatisticReconciliationActualDirectRawOwnerParity.SchemaVersion ||
            !proof.Complete || proof.Applicable != applicable ||
            proof.ProofCode !=
                (applicable ? "COMPLETE" : "DIRECT_NOT_APPLICABLE") ||
            proof.SummaryPlanBindingSha256 != plan.SemanticSha256 ||
            proof.RawProofSha256 != raw.ProofSha256 ||
            proof.DirectCaptureSha256 != direct.CaptureSemanticSha256 ||
            proof.DescriptorCount != descriptorCount ||
            proof.RawAtomCount != raw.Atoms.Count(value =>
                value.Family == "DIRECT") ||
            proof.OwnerItemCount != direct.TotalRowCount ||
            proof.RelationCount != descriptorCount ||
            semantic != proof.ProofSha256)
            throw Invalid("RELATIONAL_DIRECT_PARITY_INVALID");
    }

    private static void RequireCentralSummaryParity(
        StatisticReconciliationActualSummaryOwnerParityProof proof)
    {
        if (proof.FamilyProofs.IsDefault || proof.FamilyProofs.Length != 4 ||
            !proof.FamilyProofs.Select(value => value.Family).SequenceEqual(
                new[] { "DIRECT", "BASIC", "ADVANCED", "DIFF" },
                StringComparer.Ordinal) ||
            proof.FamilyProofs.Any(value => !value.Complete ||
                value.ProofSha256.Length != 64) ||
            proof.FamilyProofs[0].ProofCode !=
                "DIRECT_OWNER_PARITY_OUT_OF_SCOPE" ||
            proof.FamilyProofs[0].OwnerItemCount != 0 ||
            proof.FamilyProofs[0].RelationCount != 0 ||
            proof.FamilyProofs.Skip(2).Any(value => value.Applicable ||
                value.DescriptorCount != 0 || value.RawAtomCount != 0 ||
                value.OwnerItemCount != 0 || value.RelationCount != 0) ||
            proof.RawAtomCount != proof.FamilyProofs.Sum(value =>
                value.RawAtomCount) ||
            proof.OwnerItemCount != proof.FamilyProofs.Sum(value =>
                value.OwnerItemCount) ||
            proof.RelationCount != proof.FamilyProofs.Sum(value =>
                value.RelationCount))
            throw Invalid("RELATIONAL_SUMMARY_PARITY_INVALID");
        var raw = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_SUMMARY_OWNER_TOP_RAW_MANIFEST_V1",
            proof.FamilyProofs.Select(value => value.RawManifestSha256));
        var owner = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_SUMMARY_OWNER_TOP_OWNER_MANIFEST_V1",
            proof.FamilyProofs.Select(value => value.OwnerManifestSha256));
        var relation = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_SUMMARY_OWNER_TOP_RELATION_MANIFEST_V1",
            proof.FamilyProofs.Select(value => value.RelationManifestSha256));
        var family = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_SUMMARY_OWNER_FAMILY_PROOFS_V1",
            proof.FamilyProofs.Select(value => value.ProofSha256));
        var semantic = StatisticReconciliationActualCanonical.Hash(
            StatisticReconciliationActualSummaryOwnerParity.SchemaVersion,
            "COMPLETE", "~", proof.SummaryPlanBindingSha256,
            proof.RawProofSha256, family, raw, I(proof.RawAtomCount), owner,
            I(proof.OwnerItemCount), relation, I(proof.RelationCount));
        if (raw != proof.RawManifestSha256 ||
            owner != proof.OwnerManifestSha256 ||
            relation != proof.RelationManifestSha256 ||
            semantic != proof.ProofSha256)
            throw Invalid("RELATIONAL_SUMMARY_PARITY_SEMANTIC_MISMATCH");
    }

    private static void RequireExtendedParity(
        StatisticReconciliationActualExtendedRawSourceOwnerParityProof proof,
        StatisticReconciliationActualRelationalProofCommand command)
    {
        if (proof is null || proof.SchemaVersion !=
                StatisticReconciliationActualExtendedRawSourceOwnerParity
                    .SchemaVersion ||
            proof.State !=
                StatisticReconciliationActualExtendedRawSourceSchemas.Complete ||
            proof.FailureCode !=
                StatisticReconciliationActualExtendedRawSourceFailures.None ||
            !proof.RequiredPersistenceFields.IsDefaultOrEmpty ||
            proof.SummaryPlanBindingSha256 != command.SummaryPlan.SemanticSha256 ||
            proof.ExtendedRawSourceProofSha256 !=
                command.ExtendedRawSource.ProofSha256 ||
            proof.Families.IsDefault || proof.Families.Length != 2 ||
            !proof.Families.Select(value => value.Family).SequenceEqual(
                new[] { "ADVANCED", "DIFF" }, StringComparer.Ordinal))
            throw Invalid("RELATIONAL_EXTENDED_OWNER_PARITY_INVALID");

        foreach (var value in proof.Families)
        {
            var descriptors = command.SummaryPlan.IdentityDescriptors
                .Where(descriptor => descriptor.Family == value.Family)
                .ToImmutableArray();
            var applicable = descriptors.Length > 0;
            var proofCode = applicable
                ? StatisticReconciliationActualExtendedRawSourceFailures.None
                : StatisticReconciliationActualExtendedRawSourceFailures
                    .LockedMetricFamilyNotApplicable;
            var descriptorManifest =
                StatisticReconciliationActualExtendedRawSourceTypedCompiler
                    .DescriptorManifest(
                        $"P10_ACTUAL_EXTENDED_OWNER_{value.Family}_DESCRIPTORS_V1",
                        descriptors);
            var familySemantic = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_EXTENDED_OWNER_FAMILY_PARITY_V2",
                value.Family,
                applicable ? "APPLICABLE" : "NOT_APPLICABLE",
                "COMPLETE",
                proofCode,
                command.SummaryPlan.SemanticSha256,
                value.DescriptorManifestSha256,
                I(value.DescriptorCount),
                value.TypedAtomManifestSha256,
                I(value.TypedAtomCount),
                value.OwnerManifestSha256,
                I(value.OwnerItemCount),
                value.RelationManifestSha256,
                I(value.RelationCount));
            if (!value.Complete || value.Applicable != applicable ||
                value.ProofCode != proofCode ||
                value.SummaryPlanBindingSha256 !=
                    command.SummaryPlan.SemanticSha256 ||
                value.DescriptorManifestSha256 != descriptorManifest ||
                value.DescriptorCount != descriptors.Length ||
                value.TypedAtomCount < 0 || value.OwnerItemCount < 0 ||
                value.RelationCount < 0 ||
                (!applicable && (value.TypedAtomCount != 0 ||
                    value.OwnerItemCount != 0 || value.RelationCount != 0)) ||
                value.ProofSha256 != familySemantic)
                throw Invalid("RELATIONAL_EXTENDED_OWNER_PARITY_INVALID");
        }

        if (proof.TypedAtomCount !=
                proof.Families.Sum(value => value.TypedAtomCount) ||
            proof.OwnerItemCount !=
                proof.Families.Sum(value => value.OwnerItemCount) ||
            proof.RelationCount !=
                proof.Families.Sum(value => value.RelationCount))
            throw Invalid("RELATIONAL_EXTENDED_OWNER_PARITY_INVALID");
        var typed = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_EXTENDED_OWNER_TYPED_V1",
            proof.Families.Select(value => value.TypedAtomManifestSha256));
        var owner = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_EXTENDED_OWNER_ITEMS_V1",
            proof.Families.Select(value => value.OwnerManifestSha256));
        var relation = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_EXTENDED_OWNER_RELATIONS_V1",
            proof.Families.Select(value => value.RelationManifestSha256));
        var family = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_EXTENDED_OWNER_FAMILIES_V1",
            proof.Families.Select(value => value.ProofSha256));
        var semantic = StatisticReconciliationActualCanonical.Hash(
            StatisticReconciliationActualExtendedRawSourceOwnerParity
                .SchemaVersion,
            "COMPLETE", "NONE", proof.SummaryPlanBindingSha256,
            proof.ExtendedRawSourceProofSha256, family, typed,
            I(proof.TypedAtomCount), owner, I(proof.OwnerItemCount), relation,
            I(proof.RelationCount));
        if (typed != proof.TypedAtomManifestSha256 ||
            owner != proof.OwnerManifestSha256 ||
            relation != proof.RelationManifestSha256 ||
            semantic != proof.ProofSha256)
            throw Invalid(
                "RELATIONAL_EXTENDED_OWNER_PARITY_SEMANTIC_MISMATCH");
    }
    private static void RequireCrossView(
        StatisticReconciliationActualCrossViewV3OwnerResolution value,
        StatisticReconciliationActualRelationalProofCommand command)
    {
        var proof = value.Proof!;
        var authorization = value.Authorization!;
        var apiRequired = command.Api is not null;
        StatisticReconciliationActualCrossViewAuthorizationV3Integrity
            .RequireRelation(authorization, apiRequired);
        var proofSemantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CROSS_VIEW_PARITY_PROOF_V3",
            StatisticReconciliationActualCrossViewParityV3Schemas.Proof,
            authorization.SemanticSha256,
            proof.CompatibilityProofSha256,
            proof.OriginalApiSemanticSha256,
            proof.OriginalExportSemanticSha256);
        var resolution = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CROSS_VIEW_V3_OWNER_RESOLUTION_V1",
            StatisticReconciliationActualCrossViewV3OwnerSchemas.Resolution,
            StatisticReconciliationActualCrossViewV2OwnerStates.Complete,
            authorization.SemanticSha256,
            proof.ProofSha256);
        if (proof.AuthorizationRelationSha256 != authorization.SemanticSha256 ||
            proof.OriginalApiSemanticSha256 !=
                (command.Api?.CaptureSemanticSha256 ?? "API_NOT_APPLICABLE") ||
            proof.OriginalExportSemanticSha256 !=
                command.Export.CaptureSemanticSha256 ||
            proofSemantic != proof.ProofSha256 ||
            resolution != value.ResolutionSha256 ||
            value.Plan!.AuthorizationRelationSha256 !=
                authorization.SemanticSha256)
            throw Invalid("RELATIONAL_CROSS_VIEW_SEMANTIC_MISMATCH");
    }

    private static StatisticReconciliationActualRelationalProofResolution Complete(
        StatisticReconciliationActualRelationalProofBinding binding)
    {
        var resolution = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_RELATIONAL_PROOF_RESOLUTION_V3",
            StatisticReconciliationActualRelationalProofSchemas.Resolution,
            StatisticReconciliationActualRelationalProofStates.Complete,
            "NONE", binding.SemanticSha256);
        return new(
            StatisticReconciliationActualRelationalProofSchemas.Resolution,
            StatisticReconciliationActualRelationalProofStates.Complete,
            "NONE", [], binding, resolution);
    }

    private static StatisticReconciliationActualRelationalProofResolution
        Incomplete(string failure, ImmutableArray<string> required)
    {
        failure = StatisticReconciliationActualCanonical.Upper(
            failure, "RELATIONAL_FAILURE_CODE");
        required = required.IsDefault ? [] : required
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
        var manifest = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_RELATIONAL_REQUIRED_FIELDS_V2", required);
        var resolution = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_RELATIONAL_PROOF_RESOLUTION_INCOMPLETE_V3",
            StatisticReconciliationActualRelationalProofSchemas.Resolution,
            failure, manifest);
        return new(
            StatisticReconciliationActualRelationalProofSchemas.Resolution,
            StatisticReconciliationActualRelationalProofStates.Incomplete,
            failure, required, null, resolution);
    }

    private static SingleResolution SingleIncomplete(
        string failure,
        ImmutableArray<string> required)
        => new(false, failure, required.IsDefault ? [] : required, null,
            StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_RELATIONAL_SINGLE_INCOMPLETE_V2", failure));

    private static string OriginalApi(string value, string? capture)
        => capture is null
            ? value == "API_NOT_APPLICABLE"
                ? value
                : throw Invalid("RELATIONAL_API_SENTINEL_INVALID")
            : Sha(value, "RELATIONAL_XVIEW_API_SEMANTIC_SHA256");

    private static string? OptionalSha(string? value, string name)
        => value is null ? null : Sha(value, name);
    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);
    private static string I(long value)
        => StatisticReconciliationActualCanonical.Integer(value);
    private static StatisticReconciliationActualObservationException Invalid(
        string reason) => new($"RELATIONAL_PROOF_OWNER:{reason}");

    private sealed record CentralPlanProjection(
        StatisticReconciliationActualSummaryPlanBinding? Plan,
        string ProjectedPlanSha256,
        string ProjectionProofSha256);

    private sealed record CentralParityResolution(
        string ProjectedPlanSha256,
        string PlanProjectionProofSha256,
        string RawProofSha256,
        string RawSourceManifestSha256,
        int RawSourceCount,
        string RawAtomManifestSha256,
        int RawAtomCount,
        string RawDoubleCollectProofSha256,
        string ParityProofSha256,
        string ParityRawManifestSha256,
        int ParityRawAtomCount,
        string ParityOwnerManifestSha256,
        int ParityOwnerItemCount,
        string ParityRelationManifestSha256,
        int ParityRelationCount,
        string DirectParityProofSha256,
        string DirectParityOwnerManifestSha256,
        int DirectParityOwnerItemCount,
        string DirectParityRelationManifestSha256,
        int DirectParityRelationCount);
    private sealed record SingleResolution(
        bool Complete,
        string FailureCode,
        ImmutableArray<string> RequiredPersistenceFields,
        StatisticReconciliationActualRelationalProofFacts? Facts,
        string ResolutionSha256);
}
