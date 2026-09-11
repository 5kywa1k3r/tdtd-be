using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Driver;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed class StatisticReconciliationActualExtendedRawSourceOwner(
    IStatisticReconciliationActualExtendedRawSourceCollectReader reader)
    : IStatisticReconciliationActualExtendedRawSourceOwner
{
    public async Task<StatisticReconciliationActualExtendedRawSourceResolution>
        ResolveAsync(
            StatisticReconciliationActualExtendedRawSourceCommand command,
            CancellationToken cancellationToken = default)
    {
        string? planSha = null;
        try
        {
            var plan = RequireCommand(command);
            planSha = plan.SemanticSha256;
            var first = await reader.CollectAsync(command, cancellationToken)
                .ConfigureAwait(false);
            RequireCollect(first, planSha);
            var second = await reader.CollectAsync(command, cancellationToken)
                .ConfigureAwait(false);
            RequireCollect(second, planSha);
            var partitionProofs = BindPartitions(first, second);
            if (first.CollectSha256 != second.CollectSha256)
                throw new StatisticReconciliationActualExtendedRawSourceIncompleteException(
                    StatisticReconciliationActualExtendedRawSourceFailures
                        .DoubleCollectDrift);
            var partitionManifest =
                StatisticReconciliationActualCanonical.HashSequence(
                    "P10_ACTUAL_EXTENDED_RAW_SOURCE_PARTITION_DOUBLE_COLLECT_MANIFEST_V2",
                    partitionProofs);
            var doubleCollect = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_EXTENDED_RAW_SOURCE_DOUBLE_COLLECT_V2",
                planSha,
                first.CollectSha256,
                second.CollectSha256,
                partitionManifest,
                StatisticReconciliationActualCanonical.Integer(
                    partitionProofs.Length));
            var proof = StatisticReconciliationActualCanonical.Hash(
                StatisticReconciliationActualExtendedRawSourceSchemas.Resolution,
                StatisticReconciliationActualExtendedRawSourceSchemas.Complete,
                StatisticReconciliationActualExtendedRawSourceFailures.None,
                planSha,
                first.Advanced.Applicable ? "APPLICABLE" : "NOT_APPLICABLE",
                first.Advanced.Complete ? "COMPLETE" : "INCOMPLETE",
                first.Advanced.ProofCode,
                first.Advanced.ProofSha256,
                first.Advanced.GrainManifestSha256,
                StatisticReconciliationActualCanonical.Integer(
                    first.Advanced.Grains.Length),
                first.Advanced.EnvelopeManifestSha256,
                StatisticReconciliationActualCanonical.Integer(
                    first.Advanced.Envelopes.Length),
                first.Advanced.TypedAtomManifestSha256,
                StatisticReconciliationActualCanonical.Integer(
                    first.Advanced.TypedAtomCount),
                first.Diff.Applicable ? "APPLICABLE" : "NOT_APPLICABLE",
                first.Diff.Complete ? "COMPLETE" : "INCOMPLETE",
                first.Diff.ProofCode,
                first.Diff.ProofSha256,
                first.Diff.SideManifestSha256,
                StatisticReconciliationActualCanonical.Integer(
                    first.Diff.Sides.Length),
                first.Diff.EnvelopeManifestSha256,
                StatisticReconciliationActualCanonical.Integer(
                    first.Diff.Envelopes.Length),
                first.Diff.SourcePairManifestSha256,
                StatisticReconciliationActualCanonical.Integer(
                    first.Diff.SourcePairCount),
                first.Diff.TypedAtomManifestSha256,
                StatisticReconciliationActualCanonical.Integer(
                    first.Diff.TypedAtomCount),
                first.CollectSha256,
                second.CollectSha256,
                partitionManifest,
                StatisticReconciliationActualCanonical.Integer(
                    partitionProofs.Length),
                doubleCollect);
            return new(
                StatisticReconciliationActualExtendedRawSourceSchemas.Resolution,
                StatisticReconciliationActualExtendedRawSourceSchemas.Complete,
                StatisticReconciliationActualExtendedRawSourceFailures.None,
                ImmutableArray<string>.Empty,
                planSha,
                first.Advanced,
                first.Diff,
                first.CollectSha256,
                second.CollectSha256,
                partitionManifest,
                partitionProofs.Length,
                doubleCollect,
                proof);
        }
        catch (StatisticReconciliationActualExtendedRawSourceIncompleteException
               incomplete)
        {
            return Incomplete(
                incomplete.FailureCode,
                incomplete.RequiredPersistenceFields,
                planSha);
        }
        catch (Exception exception) when (exception is
                   StatisticReconciliationActualObservationException or
                   JsonException or FormatException or OverflowException or
                   MongoException or NullReferenceException or
                   KeyNotFoundException or IndexOutOfRangeException or
                   InvalidCastException or ArgumentException or
                   InvalidOperationException)
        {
            return Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .OwnerReadFailed,
                ImmutableArray<string>.Empty,
                planSha);
        }
    }

    private static StatisticReconciliationActualSummaryPlanBinding RequireCommand(
        StatisticReconciliationActualExtendedRawSourceCommand command)
    {
        if (command is null ||
            command.SchemaVersion !=
                StatisticReconciliationActualExtendedRawSourceSchemas.Command ||
            command.Material is null || command.SummaryPlan is null)
            throw new StatisticReconciliationActualExtendedRawSourceIncompleteException(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .CommandInvalid);
        StatisticReconciliationActualSummaryPlanBinding plan;
        try
        {
            plan = StatisticReconciliationActualSummaryPlanBinding.Normalize(
                command.SummaryPlan);
        }
        catch (Exception exception) when (exception is
                   ArgumentException or
                   StatisticReconciliationActualObservationException)
        {
            throw new StatisticReconciliationActualExtendedRawSourceIncompleteException(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .SummaryPlanInvalid,
                StatisticReconciliationActualExtendedRawSourceFields
                    .SummaryPlanBinding);
        }

        var capturePlan = command.Material.Run?.ActualCapturePlan;
        var v4 = StatisticReconciliationActualCapturePlanIntegrity.IsV4(
            capturePlan);
        var advancedApplicable = plan.IdentityDescriptors.Any(value =>
            value.Family == "ADVANCED");
        var diffApplicable = plan.IdentityDescriptors.Any(value =>
            value.Family == "DIFF");
        if ((!v4 && (command.Advanced is null || command.Diff is null)) ||
            (v4 && ((command.Advanced is null) == advancedApplicable ||
                    (command.Diff is null) == diffApplicable)))
            throw new StatisticReconciliationActualExtendedRawSourceIncompleteException(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .CaptureRequired);
        if ((!v4 &&
             (!SameBoundary(command.Advanced!.Boundary,
                  command.Material.Advanced!) ||
              !SameBoundary(command.Diff!.Boundary,
                  command.Material.Diff!))) ||
            (v4 && ((advancedApplicable &&
                     !SameBoundary(command.Advanced!.Boundary,
                         command.Material.Advanced!)) ||
                    (!advancedApplicable && command.Material.Advanced is not null) ||
                    (diffApplicable &&
                     !SameBoundary(command.Diff!.Boundary,
                         command.Material.Diff!)) ||
                    (!diffApplicable && command.Material.Diff is not null))))
            throw new StatisticReconciliationActualExtendedRawSourceIncompleteException(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .CommandInvalid);
        return plan;
    }

    private static bool SameBoundary(
        ActualAdvancedOwnerBoundary left,
        ActualAdvancedOwnerBoundary right)
        => left is not null && right is not null &&
           StringComparer.Ordinal.Equals(left.WorkId, right.WorkId) &&
           StringComparer.Ordinal.Equals(left.AssignmentId, right.AssignmentId) &&
           StringComparer.Ordinal.Equals(
               left.DynamicFormTemplateId, right.DynamicFormTemplateId) &&
           StringComparer.Ordinal.Equals(left.SectionId, right.SectionId) &&
           StringComparer.Ordinal.Equals(left.ConfigId, right.ConfigId) &&
           StringComparer.Ordinal.Equals(
               left.ConfigVersionId, right.ConfigVersionId) &&
           left.ConfigVersionNo == right.ConfigVersionNo &&
           left.ConfigRevision == right.ConfigRevision &&
           StringComparer.Ordinal.Equals(left.ConfigSha256, right.ConfigSha256) &&
           Same(left.DependencyPins, right.DependencyPins) &&
           StringComparer.Ordinal.Equals(left.TimeAxis, right.TimeAxis) &&
           StringComparer.Ordinal.Equals(
               left.CandidateChainId, right.CandidateChainId) &&
           StringComparer.Ordinal.Equals(
               left.CandidatePromptId, right.CandidatePromptId) &&
           left.CandidateStage == right.CandidateStage &&
           StringComparer.Ordinal.Equals(
               left.CandidateCatalogRawSha256,
               right.CandidateCatalogRawSha256) &&
           StringComparer.Ordinal.Equals(
               left.CandidateCatalogSemanticSha256,
               right.CandidateCatalogSemanticSha256) &&
           StringComparer.Ordinal.Equals(
               left.CandidateStageLockSha256,
               right.CandidateStageLockSha256) &&
           Same(left.DayNodeIds, right.DayNodeIds) &&
           Same(left.MonthNodeIds, right.MonthNodeIds) &&
           Same(left.YearNodeIds, right.YearNodeIds);

    private static bool SameBoundary(
        ActualP9DiffOwnerBoundary left,
        ActualP9DiffOwnerBoundary right)
        => left is not null && right is not null &&
           StringComparer.Ordinal.Equals(left.ResultId, right.ResultId) &&
           StringComparer.Ordinal.Equals(left.RunId, right.RunId) &&
           StringComparer.Ordinal.Equals(left.WorkId, right.WorkId) &&
           StringComparer.Ordinal.Equals(left.AssignmentId, right.AssignmentId) &&
           StringComparer.Ordinal.Equals(
               left.DynamicFormTemplateId, right.DynamicFormTemplateId) &&
           StringComparer.Ordinal.Equals(left.ConfigId, right.ConfigId) &&
           StringComparer.Ordinal.Equals(
               left.ConfigVersionId, right.ConfigVersionId) &&
           left.ConfigVersionNo == right.ConfigVersionNo &&
           left.ConfigRevision == right.ConfigRevision &&
           StringComparer.Ordinal.Equals(left.ConfigSha256, right.ConfigSha256) &&
           Same(left.DependencyPins, right.DependencyPins) &&
           StringComparer.Ordinal.Equals(
               left.CandidateChainId, right.CandidateChainId) &&
           StringComparer.Ordinal.Equals(
               left.CandidatePromptId, right.CandidatePromptId) &&
           left.CandidateStage == right.CandidateStage &&
           StringComparer.Ordinal.Equals(
               left.CandidateCatalogRawSha256,
               right.CandidateCatalogRawSha256) &&
           StringComparer.Ordinal.Equals(
               left.CandidateCatalogSemanticSha256,
               right.CandidateCatalogSemanticSha256) &&
           StringComparer.Ordinal.Equals(
               left.CandidateStageLockSha256,
               right.CandidateStageLockSha256);

    private static bool Same(
        ImmutableArray<string> left,
        ImmutableArray<string> right)
        => !left.IsDefault && !right.IsDefault &&
           left.SequenceEqual(right, StringComparer.Ordinal);

    private static void RequireCollect(
        StatisticReconciliationActualExtendedRawSourceCollect value,
        string planSha)
    {
        StatisticReconciliationActualExtendedRawSourceIntegrity
            .RequireCollect(value);
        if (value.SummaryPlanBindingSha256 != planSha)
            throw new StatisticReconciliationActualExtendedRawSourceIncompleteException(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .SummaryPlanInvalid,
                StatisticReconciliationActualExtendedRawSourceFields
                    .SummaryPlanBinding);
    }

    private static ImmutableArray<string> BindPartitions(
        StatisticReconciliationActualExtendedRawSourceCollect first,
        StatisticReconciliationActualExtendedRawSourceCollect second)
    {
        var firstPartitions = Partitions(first);
        var secondPartitions = Partitions(second);
        if (!firstPartitions.Keys.SequenceEqual(
                secondPartitions.Keys, StringComparer.Ordinal))
            throw Drift();
        var proofs = ImmutableArray.CreateBuilder<string>(
            firstPartitions.Count);
        foreach (var key in firstPartitions.Keys)
        {
            var firstSemantic = firstPartitions[key];
            var secondSemantic = secondPartitions[key];
            if (firstSemantic != secondSemantic)
                throw Drift();
            proofs.Add(StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_EXTENDED_RAW_SOURCE_PARTITION_DOUBLE_COLLECT_V2",
                key,
                firstSemantic,
                secondSemantic));
        }
        return proofs.MoveToImmutable();
    }

    private static SortedDictionary<string, string> Partitions(
        StatisticReconciliationActualExtendedRawSourceCollect collect)
    {
        var result = new SortedDictionary<string, string>(
            StringComparer.Ordinal);
        if (collect.Advanced.Applicable)
        {
            foreach (var grain in collect.Advanced.Grains)
            {
                var key = $"ADVANCED/{grain.Grain}/{grain.GrainKey}/{grain.OwnerNodeId}";
                var semantic = StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_EXTENDED_ADVANCED_PARTITION_V2",
                    grain.GrainSemanticSha256,
                    grain.SourceEnvelopeManifestSha256,
                    StatisticReconciliationActualCanonical.Integer(
                        grain.SourceEnvelopeCount),
                    grain.TypedAtomManifestSha256,
                    StatisticReconciliationActualCanonical.Integer(
                        grain.TypedAtomCount));
                if (!result.TryAdd(key, semantic))
                    throw Drift();
            }
        }
        if (collect.Diff.Applicable)
        {
            foreach (var side in collect.Diff.Sides)
            {
                var key = $"DIFF/{side.Side}";
                var semantic = StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_EXTENDED_DIFF_SIDE_PARTITION_V2",
                    side.SideSemanticSha256,
                    side.ProjectionPinManifestSha256,
                    side.EnvelopeManifestSha256,
                    side.TypedAtomManifestSha256,
                    StatisticReconciliationActualCanonical.Integer(
                        side.TypedAtomCount));
                if (!result.TryAdd(key, semantic))
                    throw Drift();
            }
            if (!result.TryAdd(
                    "DIFF/TRANSITION",
                    StatisticReconciliationActualCanonical.Hash(
                        "P10_ACTUAL_EXTENDED_DIFF_TRANSITION_PARTITION_V2",
                        collect.Diff.SourcePairManifestSha256,
                        StatisticReconciliationActualCanonical.Integer(
                            collect.Diff.SourcePairCount),
                        collect.Diff.TransitionAtomManifestSha256,
                        StatisticReconciliationActualCanonical.Integer(
                            collect.Diff.TransitionAtomCount))))
                throw Drift();
        }
        return result;
    }
    private static StatisticReconciliationActualExtendedRawSourceIncompleteException
        Drift()
        => new(
            StatisticReconciliationActualExtendedRawSourceFailures
                .DoubleCollectDrift);

    private static StatisticReconciliationActualExtendedRawSourceResolution
        Incomplete(
            string failureCode,
            ImmutableArray<string> fields,
            string? planSha)
    {
        var failure = StatisticReconciliationActualCanonical.Upper(
            failureCode, "EXTENDED_RAW_SOURCE_FAILURE_CODE");
        var normalized = fields.IsDefault
            ? ImmutableArray<string>.Empty
            : fields.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToImmutableArray();
        var fieldManifest = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_EXTENDED_RAW_SOURCE_REQUIRED_FIELDS_V2", normalized);
        var suppliedPlan = planSha is null
            ? "~"
            : StatisticReconciliationActualCanonical.Sha256(
                planSha, "EXTENDED_INCOMPLETE_PLAN_SHA");
        var proof = StatisticReconciliationActualCanonical.Hash(
            StatisticReconciliationActualExtendedRawSourceSchemas.Resolution,
            StatisticReconciliationActualExtendedRawSourceSchemas.Incomplete,
            failure,
            suppliedPlan,
            fieldManifest,
            StatisticReconciliationActualCanonical.Integer(normalized.Length));
        return new(
            StatisticReconciliationActualExtendedRawSourceSchemas.Resolution,
            StatisticReconciliationActualExtendedRawSourceSchemas.Incomplete,
            failure,
            normalized,
            planSha ?? proof,
            null,
            null,
            proof,
            proof,
            proof,
            0,
            proof,
            proof);
    }
}
