namespace tdtd_be.DTOs.StatisticsReconciliation;

public sealed record StatisticReconciliationExpectedPermissionSnapshotResponse(
    string ActorUserId,
    string PermissionCode,
    bool DetailedProvenanceAllowed,
    long RowCountBeforeRedaction,
    long RowCountAfterRedaction);

public sealed record StatisticReconciliationExpectedBusinessMetricRowResponse(
    string Family,
    string Kind,
    string AtomKind,
    string ValueType,
    string ValueState,
    long AtomCount);

public sealed record StatisticReconciliationExpectedBusinessSummaryResponse(
    string ReconciliationId,
    string GenerationId,
    string CompilationStatus,
    string Ordering,
    string RedactionPolicy,
    StatisticReconciliationExpectedPermissionSnapshotResponse Permission,
    IReadOnlyList<StatisticReconciliationExpectedBusinessMetricRowResponse> Rows,
    long Total);

public sealed record StatisticReconciliationExpectedProvenancePageResponse(
    string ReconciliationId,
    string GenerationId,
    string ManifestSha256,
    string Ordering,
    StatisticReconciliationExpectedPermissionSnapshotResponse Permission,
    IReadOnlyList<StatisticReconciliationExpectedProvenanceRowResponse> Rows,
    long Total,
    int PageSize,
    string? NextCursor);

public sealed record StatisticReconciliationExpectedProvenanceRowResponse(
    string RecordId,
    string RecordKind,
    StatisticReconciliationExpectedObservationHeaderResponse Header,
    StatisticReconciliationExpectedSourceDecisionResponse? SourceDecision,
    StatisticReconciliationExpectedConfigurationPinResponse? ConfigurationPin,
    StatisticReconciliationExpectedLineagePinResponse? LineagePin,
    StatisticReconciliationExpectedAtomResponse? Atom,
    StatisticReconciliationExpectedCommitResponse? Commit,
    string DocumentSemanticSha256);

public sealed record StatisticReconciliationExpectedObservationHeaderResponse(
    string SchemaVersion,
    string ReconciliationId,
    string ImmutableIdentitySha256,
    string ImmutableHeaderSha256,
    string GenerationId,
    string GenerationSemanticSha256,
    string AlgorithmRevision,
    string AlgorithmSha256,
    string SourceSetSha256,
    string TypedSemanticSha256,
    string MetricPlanSha256,
    string InputBindingSha256,
    string LifecycleSemanticSha256,
    string ContributionSemanticSha256,
    string? TenantUnitId,
    string WorkId,
    string ScopeAssignmentId,
    string PeriodKey,
    string PeriodInstanceKey,
    string ConceptKey,
    string Grain,
    string TimeAxis,
    string FilterSha256,
    string DynamicFormVersionId,
    string DynamicFormSchemaSha256,
    string FlowTemplateVersionId,
    string FlowPayloadSha256,
    string FlowInstanceId,
    string ExecutionEpochId,
    int ExecutionEpoch,
    long ExecutionEpochRevision,
    string P8ConfigurationOwnerId,
    string P8ConfigurationBundleSha256,
    StatisticReconciliationExpectedCatalogPinsResponse CatalogPins);

public sealed record StatisticReconciliationExpectedCatalogPinsResponse(
    string P9CatalogVersion,
    string P9CatalogRawSha256,
    string P9CatalogSemanticSha256,
    string P9SchemaRawSha256,
    string P9SchemaSemanticSha256,
    string P9StageLockSha256,
    string CandidateChainId,
    string CandidatePromptId,
    string CandidateCatalogVersion,
    string CandidateCatalogRawSha256,
    string CandidateCatalogSemanticSha256,
    string CandidateSchemaRawSha256,
    string CandidateSchemaSemanticSha256,
    string CandidateStageLockSha256,
    string CatalogPinSetSha256);

public sealed record StatisticReconciliationExpectedSourceDecisionResponse(
    string StableSourceId,
    string IdentityKey,
    string ReportId,
    string PayloadDocumentId,
    int PayloadRevision,
    string PayloadOwnerSha256,
    string PayloadCanonicalSha256,
    int LifecycleRevision,
    string LifecycleSha256,
    string LifecycleStatus,
    bool IsEffective,
    bool IsLocked,
    string RuntimeDisposition,
    string Disposition,
    string ReasonCode,
    string ContributionPolicy,
    string ContributionVersionId,
    long ContributionRevision,
    string ContributionPolicySha256,
    string ContributionProvenanceId,
    string ContributionProvenanceSha256,
    string DecisionSemanticSha256);

public sealed record StatisticReconciliationExpectedConfigurationPinResponse(
    string Kind,
    string OwnerId,
    string ConfigId,
    string VersionId,
    int VersionNo,
    long Revision,
    string ConfigSha256);

public sealed record StatisticReconciliationExpectedLineagePinResponse(
    string Layer,
    string OwnerId,
    string VersionId,
    long Revision,
    string Sha256);

public sealed record StatisticReconciliationExpectedAtomResponse(
    string IdentitySha256,
    string Family,
    string Kind,
    string MetricId,
    string? FieldId,
    string? TableId,
    string? RowId,
    string? LabelId,
    string? BasicScope,
    string? BasicScopeId,
    string? FlowBranchId,
    string? FlowStepId,
    string? AdvancedGrain,
    string? DiffKind,
    string? PeriodKey,
    string AtomKind,
    string ValueType,
    string ValueState,
    string CanonicalValue,
    int DecimalScale,
    long OccurrenceCount,
    long ReportCount,
    long RowCount,
    long NumericValueCount,
    string ValueIdentitySha256,
    string AtomSemanticSha256);

public sealed record StatisticReconciliationExpectedCommitResponse(
    int SourceDecisionCount,
    int ConfigurationPinCount,
    int LineagePinCount,
    int AtomCount,
    int DocumentCount,
    string ManifestSha256);
