using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record StatisticReconciliationActualCrossViewBasicGenerationPreimage(
    string SnapshotRequestSha256,
    string SnapshotRequestJsonSha256,
    string SourceSignatureSha256,
    string ConfigId,
    string ConfigVersionId,
    int ConfigVersionNo,
    int ConfigRevision,
    string ConfigSha256,
    ImmutableArray<string> ConfigDependencyPins,
    string CandidateChainId,
    string CandidatePromptId,
    int CandidateStage,
    string CandidateCatalogRawSha256,
    string CandidateCatalogSemanticSha256,
    string CandidateStageLockSha256,
    ImmutableArray<string> AssignmentIds,
    ImmutableArray<string> ReportIds);

internal sealed partial class StatisticReconciliationActualCrossViewParityV2
{
    private static string? RequireBasicGenerationBinding(
        StatisticReconciliationActualCrossViewParityV2Plan value,
        string family,
        string? apiGenerationId,
        string? apiGenerationSha256,
        string exportResultSha256,
        string exportConfigSha256,
        string exportSourceSha256,
        int exportLifecycleRevision)
    {
        if (family is not (
            StatisticReconciliationActualCrossViewFamilies.Basic or
            StatisticReconciliationActualCrossViewFamilies.Flow))
        {
            if (value.BasicGeneration is not null)
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .PlanInvalid);
            return null;
        }

        var input = value.BasicGeneration ?? throw Fail(
            StatisticReconciliationActualCrossViewParityV2Failures
                .PreimageMissing);
        var requestSha = Sha(input.SnapshotRequestSha256);
        var requestJsonSha = Sha(input.SnapshotRequestJsonSha256);
        var sourceSha = Sha(input.SourceSignatureSha256);
        var configId = Required(input.ConfigId);
        var configVersionId = Required(input.ConfigVersionId);
        var configSha = Sha(input.ConfigSha256);
        var chainId = Required(input.CandidateChainId);
        var promptId = Required(input.CandidatePromptId);
        var catalogRawSha = Sha(input.CandidateCatalogRawSha256);
        var catalogSemanticSha = Sha(input.CandidateCatalogSemanticSha256);
        var stageLockSha = Sha(input.CandidateStageLockSha256);
        if (input.ConfigVersionNo < 0 || input.ConfigRevision < 0 ||
            input.CandidateStage < 0 || input.ConfigDependencyPins.IsDefault ||
            input.AssignmentIds.IsDefault || input.ReportIds.IsDefault ||
            input.ConfigDependencyPins.Length > 10_000 ||
            input.AssignmentIds.Length > MaximumRows ||
            input.ReportIds.Length > MaximumRows)
        {
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .PlanInvalid);
        }

        var dependencies = input.ConfigDependencyPins
            .Select(Required)
            .ToImmutableArray();
        var assignmentIds = RequireCanonicalIdSequence(input.AssignmentIds);
        var reportIds = RequireCanonicalIdSequence(input.ReportIds);
        var generationSha = H(
            "P10_ACTUAL_API_BASIC_GENERATION_V1",
            apiGenerationId,
            requestSha,
            requestJsonSha,
            sourceSha,
            configId,
            configVersionId,
            I(input.ConfigVersionNo),
            I(input.ConfigRevision),
            configSha,
            HS("P10_ACTUAL_API_BASIC_CONFIG_DEPENDENCIES_V1", dependencies),
            chainId,
            promptId,
            I(input.CandidateStage),
            catalogRawSha,
            catalogSemanticSha,
            stageLockSha,
            exportResultSha256,
            HS("P10_ACTUAL_API_BASIC_ASSIGNMENT_IDS_V1", assignmentIds),
            HS("P10_ACTUAL_API_BASIC_REPORT_IDS_V1", reportIds));
        if (!Eq(generationSha, apiGenerationSha256) ||
            !Eq(sourceSha, exportSourceSha256) ||
            !Eq(configSha, exportConfigSha256) ||
            exportLifecycleRevision != 0)
        {
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .ResultMismatch);
        }

        return H(
            "P10_ACTUAL_CROSS_VIEW_BASIC_GENERATION_PREIMAGE_V2",
            generationSha,
            requestSha,
            requestJsonSha,
            sourceSha,
            configId,
            configVersionId,
            I(input.ConfigVersionNo),
            I(input.ConfigRevision),
            configSha,
            HS("P10_ACTUAL_CROSS_VIEW_BASIC_DEPENDENCIES_V2", dependencies),
            chainId,
            promptId,
            I(input.CandidateStage),
            catalogRawSha,
            catalogSemanticSha,
            stageLockSha,
            HS("P10_ACTUAL_CROSS_VIEW_BASIC_ASSIGNMENTS_V2", assignmentIds),
            HS("P10_ACTUAL_CROSS_VIEW_BASIC_REPORTS_V2", reportIds));
    }

    private static ImmutableArray<string> RequireCanonicalIdSequence(
        ImmutableArray<string> values)
    {
        var result = values.Select(Required).ToImmutableArray();
        for (var index = 1; index < result.Length; index++)
        {
            if (StringComparer.Ordinal.Compare(
                    result[index - 1], result[index]) >= 0)
            {
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .PlanInvalid);
            }
        }
        return result;
    }
}
