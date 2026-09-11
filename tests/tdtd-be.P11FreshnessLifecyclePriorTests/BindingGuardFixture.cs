using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class BindingGuardFixture
{
    internal static void PopulateCreationContext(StatisticReconciliationRun run)
    {
        var capture = run.CurrentGenerationRecheckCaptureBinding!;
        run.ImmutableIdentityHash = Fixture.Sha("creation_identity");
        run.ImmutableHeaderHash = Fixture.Sha("creation_header");
        run.FilterHash = Fixture.Sha("creation_filter");
        run.ConceptKey = "DIRECT_VALUE"; run.Grain = "ASSIGNMENT";
        run.P8ConfigOwnerId = capture.P8ConfigOwnerId;
        run.P8ConfigBundleHash = capture.P8ConfigBundleHash;
        run.ActualCapturePlan = capture.ActualCapturePlan;
        run.ActualConfigurationBundleSha256 = capture.ActualConfigurationBundleSha256;
        run.P9CatalogVersion = capture.P9CatalogVersion;
        run.P9CatalogRawSha256 = capture.P9CatalogRawSha256;
        run.P9CatalogSemanticSha256 = capture.P9CatalogSemanticSha256;
        run.P9SchemaRawSha256 = capture.P9SchemaRawSha256;
        run.P9SchemaSemanticSha256 = capture.P9SchemaSemanticSha256;
        run.P9StageLockSha256 = capture.P9StageLockSha256;
    }

    internal static StatisticReconciliationActualPublicationContext Context(StatisticReconciliationRun run)
    {
        var pinHash = StatisticReconciliationActualCanonical.Hash("P10_ACTUAL_CATALOG_PIN_SET_V1",
            run.P9CatalogVersion, run.P9CatalogRawSha256, run.P9CatalogSemanticSha256,
            run.P9SchemaRawSha256, run.P9SchemaSemanticSha256, run.P9StageLockSha256,
            run.CandidateChainId, run.CandidatePromptId, run.CandidateCatalogVersion,
            run.CandidateCatalogRawSha256, run.CandidateCatalogSemanticSha256,
            run.CandidateSchemaRawSha256, run.CandidateSchemaSemanticSha256, run.CandidateStageLockSha256);
        return new(run.Id, run.ImmutableIdentityHash, run.ImmutableHeaderHash, run.WorkId,
            run.ScopeAssignmentId, run.PeriodKey, run.PeriodInstanceKey, run.ConceptKey, run.Grain,
            run.TimeAxis, run.FilterHash, run.DynamicFormVersionId, run.DynamicFormSchemaHash ?? throw new InvalidOperationException("FIXTURE_SCHEMA_REQUIRED"),
            run.FlowTemplateVersionId, run.FlowPayloadHash, run.FlowInstanceId, run.FlowExecutionEpochId,
            run.FlowExecutionEpoch, run.FlowExecutionEpochRevision, run.P8ConfigOwnerId ?? throw new InvalidOperationException("FIXTURE_P8_OWNER_REQUIRED"),
            run.P8ConfigBundleHash, run.ActualCapturePlan is null ? run.P8ConfigBundleHash : run.ActualConfigurationBundleSha256 ?? throw new InvalidOperationException("FIXTURE_ACTUAL_CONFIG_REQUIRED"),
            Fixture.Sha("coherent_catalog"), new(run.P9CatalogVersion, run.P9CatalogRawSha256,
                run.P9CatalogSemanticSha256, run.P9SchemaRawSha256, run.P9SchemaSemanticSha256,
                run.P9StageLockSha256, run.CandidateChainId, run.CandidatePromptId, run.CandidateCatalogVersion,
                run.CandidateCatalogRawSha256, run.CandidateCatalogSemanticSha256, run.CandidateSchemaRawSha256,
                run.CandidateSchemaSemanticSha256, run.CandidateStageLockSha256, pinHash),
            LifecycleMetricScopeSha256: Fixture.Sha("metric_scope"));
    }
}
