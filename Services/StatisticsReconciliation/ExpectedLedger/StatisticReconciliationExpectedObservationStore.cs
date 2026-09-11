using System.Globalization;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

public static class StatisticReconciliationExpectedObservationFailureReasons
{
    public const string GenerationInvalid = "P10_EXPECTED_OBSERVATION_GENERATION_INVALID";
    public const string GenerationConflict = "P10_EXPECTED_OBSERVATION_GENERATION_CONFLICT";
    public const string GenerationIncomplete = "P10_EXPECTED_OBSERVATION_GENERATION_INCOMPLETE";
}

public sealed class StatisticReconciliationExpectedObservationException
    : InvalidOperationException
{
    public StatisticReconciliationExpectedObservationException(
        string reasonCode,
        string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

public sealed record StatisticReconciliationExpectedObservationAppendResult(
    string ReconciliationId,
    string GenerationId,
    string GenerationSemanticSha256,
    string ManifestSha256,
    int DocumentCount,
    bool ExactReplay,
    string MetricPlanSha256,
    int MetricPlanEntryCount,
    string MembershipSemanticSha256,
    string RuntimeKind)
{
    internal StatisticReconciliationExpectedGenerationBinding ExactBinding
        => new(
            ReconciliationId,
            GenerationId,
            GenerationSemanticSha256,
            MetricPlanSha256,
            MetricPlanEntryCount,
            ManifestSha256,
            DocumentCount,
            MembershipSemanticSha256,
            RuntimeKind);
}

public interface IStatisticReconciliationExpectedObservationStore
{
    Task<StatisticReconciliationExpectedObservationAppendResult> AppendGenerationAsync(
        StatisticReconciliationExpectedCompiledGeneration generation,
        DateTime createdAtUtc,
        CancellationToken cancellationToken = default);
}

internal sealed record StatisticReconciliationExpectedStoredObservation(
    string Id,
    string RecordKind,
    string DocumentSemanticSha256);

internal interface IStatisticReconciliationExpectedObservationBackend
{
    Task<IReadOnlyList<StatisticReconciliationExpectedStoredObservation>> ReadGenerationAsync(
        string reconciliationId,
        string generationId,
        CancellationToken cancellationToken);

    Task AppendContentAsync(
        IReadOnlyList<StatisticReconciliationObservation> observations,
        CancellationToken cancellationToken);

    Task AppendCommitAsync(
        StatisticReconciliationObservation observation,
        CancellationToken cancellationToken);
}

internal sealed class StatisticReconciliationExpectedObservationMongoBackend(
    MongoDbContext context)
    : IStatisticReconciliationExpectedObservationBackend
{
    public async Task<IReadOnlyList<StatisticReconciliationExpectedStoredObservation>>
        ReadGenerationAsync(
            string reconciliationId,
            string generationId,
            CancellationToken cancellationToken)
    {
        var documents = await context.StatisticReconciliationObservations
            .Find(item =>
                item.ReconciliationId == reconciliationId &&
                item.GenerationId == generationId &&
                item.SchemaVersion ==
                StatisticReconciliationExpectedObservationIntegrity
                    .ObservationSchemaVersion &&
                (item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.SourceDecision ||
                 item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.ConfigurationPin ||
                 item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.LineagePin ||
                 item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds
                         .ExpectedMetricPlan ||
                 item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.ExpectedAtom ||
                 item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.GenerationCommit))
            .ToListAsync(cancellationToken);
        return documents
            .Select(item => new StatisticReconciliationExpectedStoredObservation(
                item.Id,
                item.RecordKind,
                item.DocumentSemanticSha256))
            .ToArray();
    }

    public async Task AppendContentAsync(
        IReadOnlyList<StatisticReconciliationObservation> observations,
        CancellationToken cancellationToken)
    {
        if (observations.Count == 0)
            return;
        try
        {
            await context.StatisticReconciliationObservations.InsertManyAsync(
                observations,
                new InsertManyOptions { IsOrdered = false },
                cancellationToken);
        }
        catch (MongoBulkWriteException<StatisticReconciliationObservation> error)
            when (error.WriteErrors.Count > 0 &&
                  error.WriteErrors.All(item =>
                      item.Category == ServerErrorCategory.DuplicateKey))
        {
            // Exact replay is verified by the owner after the append attempt.
        }
    }

    public async Task AppendCommitAsync(
        StatisticReconciliationObservation observation,
        CancellationToken cancellationToken)
    {
        try
        {
            await context.StatisticReconciliationObservations.InsertOneAsync(
                observation,
                cancellationToken: cancellationToken);
        }
        catch (MongoWriteException error)
            when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // A racing exact replay is verified by the owner below.
        }
    }
}

internal sealed class StatisticReconciliationExpectedObservationStore(
    IStatisticReconciliationExpectedObservationBackend backend,
    IStatisticReconciliationExpectedMetricIdentityCompiler identityCompiler)
    : IStatisticReconciliationExpectedObservationStore
{
    private const string ObservationSchemaVersion =
        StatisticReconciliationExpectedObservationIntegrity
            .ObservationSchemaVersion;

    public async Task<StatisticReconciliationExpectedObservationAppendResult>
        AppendGenerationAsync(
            StatisticReconciliationExpectedCompiledGeneration generation,
            DateTime createdAtUtc,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (createdAtUtc.Kind != DateTimeKind.Utc)
            throw Invalid("createdAtUtc must be UTC.");

        ValidateGeneration(generation);
        var content = BuildContent(generation, createdAtUtc);
        EnsureUniqueDocuments(content);
        var manifestSha256 = H(
            "P10_EXPECTED_OBSERVATION_MANIFEST_V1",
            content
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => item.DocumentSemanticSha256));
        var commit = BuildCommit(
            generation,
            createdAtUtc,
            manifestSha256,
            content);
        var expected = content
            .Append(commit)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);

        var before = await backend.ReadGenerationAsync(
            generation.ContextPin.ReconciliationId,
            generation.GenerationId,
            cancellationToken);
        ValidateExisting(before, expected, allowIncomplete: true);
        if (HasCommit(before))
        {
            ValidateExisting(before, expected, allowIncomplete: false);
            return Result(generation, manifestSha256, expected.Count, exactReplay: true);
        }

        await backend.AppendContentAsync(content, cancellationToken);
        var afterContent = await backend.ReadGenerationAsync(
            generation.ContextPin.ReconciliationId,
            generation.GenerationId,
            cancellationToken);
        ValidateExisting(afterContent, expected, allowIncomplete: true);
        EnsureAllContentPresent(content, afterContent);
        if (!HasCommit(afterContent))
            await backend.AppendCommitAsync(commit, cancellationToken);

        var completed = await backend.ReadGenerationAsync(
            generation.ContextPin.ReconciliationId,
            generation.GenerationId,
            cancellationToken);
        ValidateExisting(completed, expected, allowIncomplete: false);
        return Result(
            generation,
            manifestSha256,
            expected.Count,
            exactReplay: before.Count > 0);
    }

    private IReadOnlyList<StatisticReconciliationObservation> BuildContent(
        StatisticReconciliationExpectedCompiledGeneration generation,
        DateTime createdAtUtc)
    {
        var content = new List<StatisticReconciliationObservation>();
        foreach (var decision in generation.SourcePlan.SourceDecisions
                     .OrderBy(item => item.DecisionSemanticSha256, StringComparer.Ordinal))
        {
            var document = Common(
                generation,
                StatisticReconciliationObservationRecordKinds.SourceDecision,
                createdAtUtc);
            document.SourceDecision = new StatisticReconciliationObservationSourceDecision
            {
                StableSourceId = decision.StableSourceId,
                IdentityKey = decision.Identity.IdentityKey,
                ReportId = decision.Identity.ReportId,
                WorkAssignmentId = decision.Identity.WorkAssignmentId,
                SourceStableIdentitySha256 =
                    StatisticReconciliationExpectedMembershipIntegrity
                        .BuildStableIdentitySha256(
                            decision.Identity.WorkId,
                            decision.Identity.WorkAssignmentId,
                            decision.Identity.ReportId),
                PayloadDocumentId = decision.PayloadDocumentId,
                PayloadRevision = decision.Identity.PayloadRevision,
                PayloadOwnerSha256 = decision.Identity.PayloadOwnerSha256,
                PayloadCanonicalSha256 = decision.Identity.PayloadCanonicalSha256,
                LifecycleRevision = decision.Identity.LifecycleRevision,
                LifecycleSha256 = decision.Identity.LifecycleSha256,
                LifecycleStatus = decision.LifecycleStatus,
                IsEffective = decision.IsEffective,
                IsLocked = decision.IsLocked,
                RuntimeDisposition = decision.RuntimeDisposition,
                Disposition = decision.Disposition,
                ReasonCode = decision.ReasonCode,
                ContributionPolicy = decision.ContributionPolicy,
                ContributionVersionId = decision.ContributionVersionId,
                ContributionRevision = decision.ContributionRevision,
                ContributionPolicySha256 = decision.ContributionPolicySha256,
                ContributionProvenanceId = decision.ContributionProvenanceId,
                ContributionProvenanceSha256 = decision.ContributionProvenanceSha256,
                DecisionSemanticSha256 = decision.DecisionSemanticSha256,
                AuthoritativeRuntime = Runtime(decision.RuntimePin)
            };
            document.Id = H(
                "P10_EXPECTED_OBSERVATION_SOURCE_ID_V1",
                generation.ContextPin.ReconciliationId,
                generation.GenerationId,
                decision.StableSourceId,
                I(decision.Identity.PayloadRevision),
                I(decision.Identity.LifecycleRevision));
            document.DocumentSemanticSha256 = DocumentHash(
                document,
                decision.DecisionSemanticSha256,
                decision.Identity.IdentityKey,
                decision.Identity.ReportId,
                decision.Identity.WorkAssignmentId,
                document.SourceDecision.SourceStableIdentitySha256!,
                decision.PayloadDocumentId,
                decision.Identity.PayloadOwnerSha256,
                decision.Identity.PayloadCanonicalSha256,
                decision.Identity.LifecycleSha256,
                decision.RuntimePin?.RuntimeSemanticSha256 ?? "~");
            content.Add(document);
        }

        foreach (var pin in generation.SourcePlan.BoundInputs.LockedP8Configuration.Pins
                     .OrderBy(item => item.Kind, StringComparer.Ordinal)
                     .ThenBy(item => item.ConfigId, StringComparer.Ordinal))
        {
            var document = Common(
                generation,
                StatisticReconciliationObservationRecordKinds.ConfigurationPin,
                createdAtUtc);
            document.ConfigurationPin = new StatisticReconciliationObservationConfigurationPin
            {
                Kind = pin.Kind,
                OwnerId = pin.OwnerId,
                ConfigId = pin.ConfigId,
                VersionId = pin.VersionId,
                VersionNo = pin.VersionNo,
                Revision = pin.Revision,
                ConfigSha256 = pin.ConfigSha256
            };
            document.Id = H(
                "P10_EXPECTED_OBSERVATION_CONFIG_ID_V1",
                generation.ContextPin.ReconciliationId,
                generation.GenerationId,
                pin.Kind,
                pin.ConfigId,
                pin.VersionId,
                I(pin.Revision));
            document.DocumentSemanticSha256 = DocumentHash(
                document,
                pin.Kind,
                pin.OwnerId,
                pin.ConfigId,
                pin.VersionId,
                I(pin.VersionNo),
                I(pin.Revision),
                pin.ConfigSha256);
            content.Add(document);
        }

        foreach (var pin in generation.SourcePlan.BoundInputs
                     .RuntimeMappingContributionLineage.Pins
                     .OrderBy(item => item.Layer, StringComparer.Ordinal)
                     .ThenBy(item => item.OwnerId, StringComparer.Ordinal))
        {
            var document = Common(
                generation,
                StatisticReconciliationObservationRecordKinds.LineagePin,
                createdAtUtc);
            document.LineagePin = new StatisticReconciliationObservationLineagePin
            {
                Layer = pin.Layer,
                OwnerId = pin.OwnerId,
                VersionId = pin.VersionId,
                Revision = pin.Revision,
                Sha256 = pin.Sha256
            };
            document.Id = H(
                "P10_EXPECTED_OBSERVATION_LINEAGE_ID_V1",
                generation.ContextPin.ReconciliationId,
                generation.GenerationId,
                pin.Layer,
                pin.OwnerId,
                pin.VersionId,
                I(pin.Revision));
            document.DocumentSemanticSha256 = DocumentHash(
                document,
                pin.Layer,
                pin.OwnerId,
                pin.VersionId,
                I(pin.Revision),
                pin.Sha256);
            content.Add(document);
        }

        foreach (var entry in generation.MetricPlan)
        {
            var identity = entry.Identity;
            var document = Common(
                generation,
                StatisticReconciliationObservationRecordKinds.ExpectedMetricPlan,
                createdAtUtc);
            document.MetricPlan = new StatisticReconciliationObservationMetricPlan
            {
                SchemaVersion = entry.SchemaVersion,
                IdentitySha256 = identity.IdentitySha256,
                Family = identity.Family,
                Kind = identity.Kind,
                MetricId = identity.MetricId,
                FieldId = identity.FieldId,
                TableId = identity.TableId,
                RowId = identity.RowId,
                LabelId = identity.LabelId,
                BasicScope = identity.ScopeKind,
                BasicScopeId = identity.ScopeId,
                AdvancedGrain = identity.Grain,
                DiffKind = identity.DiffKind,
                PeriodKey = identity.PeriodKey,
                JsonPointer = entry.JsonPointer,
                ValueType = entry.ValueType,
                Unordered = entry.Unordered,
                ExpandArray = entry.ExpandArray,
                Operations = entry.Operations.ToList(),
                TransitionMode = entry.TransitionMode,
                BeforeJsonPointer = entry.BeforeJsonPointer,
                AfterJsonPointer = entry.AfterJsonPointer,
                DifferenceOperation = entry.DifferenceOperation,
                TransitionKind = entry.TransitionKind,
                PlanEntrySha256 = entry.PlanEntrySha256
            };
            document.Id = H(
                "P10_EXPECTED_OBSERVATION_METRIC_PLAN_ID_V2",
                generation.ContextPin.ReconciliationId,
                generation.GenerationId,
                identity.IdentitySha256);
            document.DocumentSemanticSha256 = DocumentHash(
                document,
                entry.SchemaVersion,
                identity.IdentitySha256,
                identity.Family,
                identity.Kind,
                identity.MetricId,
                identity.PeriodKey,
                identity.FieldId ?? "~",
                identity.TableId ?? "~",
                identity.RowId ?? "~",
                identity.LabelId ?? "~",
                identity.ScopeKind ?? "~",
                identity.ScopeId ?? "~",
                identity.Grain ?? "~",
                identity.DiffKind ?? "~",
                entry.JsonPointer,
                entry.ValueType,
                entry.Unordered ? "1" : "0",
                entry.ExpandArray ? "1" : "0",
                H("P10_EXPECTED_OPERATIONS_V2", entry.Operations),
                entry.TransitionMode,
                entry.BeforeJsonPointer ?? "~",
                entry.AfterJsonPointer ?? "~",
                entry.DifferenceOperation ?? "~",
                entry.TransitionKind ?? "~",
                entry.PlanEntrySha256);
            content.Add(document);
        }

        foreach (var atom in generation.Atoms)
        {
            var identity = atom.Identity;
            var document = Common(
                generation,
                StatisticReconciliationObservationRecordKinds.ExpectedAtom,
                createdAtUtc);
            document.Atom = new StatisticReconciliationObservationAtom
            {
                IdentitySha256 = identity.IdentitySha256,
                Family = identity.Family,
                Kind = identity.Kind,
                MetricId = identity.MetricId,
                PeriodKey = identity.PeriodKey,
                FieldId = identity.FieldId,
                TableId = identity.TableId,
                RowId = identity.RowId,
                LabelId = identity.LabelId,
                BasicScope = identity.ScopeKind,
                BasicScopeId = identity.ScopeId,
                FlowBranchId = identity.ScopeKind ==
                    StatisticReconciliationExpectedBasicScopes.FlowBranch
                    ? identity.ScopeId
                    : null,
                FlowStepId = identity.ScopeKind ==
                    StatisticReconciliationExpectedBasicScopes.FlowStep
                    ? identity.ScopeId
                    : null,
                AdvancedGrain = identity.Grain,
                DiffKind = identity.DiffKind,
                AtomKind = atom.AtomKind,
                ValueType = atom.ValueType,
                ValueState = atom.ValueState,
                CanonicalValue = atom.CanonicalValue,
                DecimalScale = atom.DecimalScale,
                OccurrenceCount = atom.OccurrenceCount,
                ReportCount = atom.ReportCount,
                RowCount = atom.RowCount,
                NumericValueCount = atom.NumericValueCount,
                ValueIdentitySha256 = atom.ValueIdentitySha256,
                AtomSemanticSha256 = atom.AtomSemanticSha256,
                TransitionLeg = atom.TransitionLeg,
                TransitionKind = atom.TransitionKind,
                CollectionSemantics = atom.CollectionSemantics
            };
            document.Id = H(
                "P10_EXPECTED_OBSERVATION_ATOM_ID_V2",
                generation.ContextPin.ReconciliationId,
                generation.GenerationId,
                identity.IdentitySha256,
                atom.AtomKind,
                atom.ValueIdentitySha256);
            document.DocumentSemanticSha256 = DocumentHash(
                document,
                atom.AtomSemanticSha256,
                identity.IdentitySha256,
                atom.TransitionLeg,
                atom.TransitionKind ?? "~",
                atom.CollectionSemantics ?? "~",
                atom.ValueIdentitySha256);
            content.Add(document);
        }
        return content;
    }

    private static StatisticReconciliationObservationAuthoritativeRuntimePin?
        Runtime(StatisticReconciliationExpectedAuthoritativeRuntimePin? value)
        => value is null
            ? null
            : new()
            {
                RuntimeKind = value.RuntimeKind,
                FlowTemplateVersionId = value.FlowTemplateVersionId,
                FlowPayloadSha256 = value.FlowPayloadSha256,
                FlowInstanceId = value.FlowInstanceId,
                FlowBranchId = value.FlowBranchId,
                FlowStepId = value.FlowStepId,
                FlowStepInstanceId = value.FlowStepInstanceId,
                FlowStepRevision = value.FlowStepRevision,
                FlowAttemptNo = value.FlowAttemptNo,
                ExecutionEpochId = value.ExecutionEpochId,
                ExecutionEpoch = value.ExecutionEpoch,
                CurrentExecutionEpochId = value.CurrentExecutionEpochId,
                CurrentExecutionEpoch = value.CurrentExecutionEpoch,
                ExecutionEpochRevision = value.ExecutionEpochRevision,
                IsCanonicalEpoch = value.IsCanonicalEpoch,
                ApprovalCommandId = value.ApprovalCommandId,
                ApprovalEventKey = value.ApprovalEventKey,
                MappingReceiptId = value.MappingReceiptId,
                MappingProvenanceId = value.MappingProvenanceId,
                MappingProvenanceSha256 = value.MappingProvenanceSha256,
                MappingResultSemanticSha256 =
                    value.MappingResultSemanticSha256,
                MappingResultPayloadRevision =
                    value.MappingResultPayloadRevision,
                MappingResultPayloadSha256 = value.MappingResultPayloadSha256,
                MappingFlowVersionId = value.MappingFlowVersionId,
                MappingFlowVersionNo = value.MappingFlowVersionNo,
                MappingFlowPayloadSha256 = value.MappingFlowPayloadSha256,
                MappingLocked = value.MappingLocked,
                ConfigVersionId = value.ConfigVersionId,
                ConfigSha256 = value.ConfigSha256,
                MembershipSignatureSha256 = value.MembershipSignatureSha256,
                ContributionPolicy = value.ContributionPolicy,
                ContributionPolicySha256 = value.ContributionPolicySha256,
                ContributionProvenanceId = value.ContributionProvenanceId,
                ContributionProvenanceSha256 =
                    value.ContributionProvenanceSha256,
                LifecycleOwnerSha256 = value.LifecycleOwnerSha256,
                RuntimeSemanticSha256 = value.RuntimeSemanticSha256
            };
    private static StatisticReconciliationObservation BuildCommit(
        StatisticReconciliationExpectedCompiledGeneration generation,
        DateTime createdAtUtc,
        string manifestSha256,
        IReadOnlyList<StatisticReconciliationObservation> content)
    {
        var decisionCount = content.Count(item =>
            item.RecordKind == StatisticReconciliationObservationRecordKinds.SourceDecision);
        var configurationCount = content.Count(item =>
            item.RecordKind == StatisticReconciliationObservationRecordKinds.ConfigurationPin);
        var lineageCount = content.Count(item =>
            item.RecordKind == StatisticReconciliationObservationRecordKinds.LineagePin);
        var metricPlanCount = content.Count(item =>
            item.RecordKind ==
                StatisticReconciliationObservationRecordKinds.ExpectedMetricPlan);
        var atomCount = content.Count(item =>
            item.RecordKind == StatisticReconciliationObservationRecordKinds.ExpectedAtom);
        var document = Common(
            generation,
            StatisticReconciliationObservationRecordKinds.GenerationCommit,
            createdAtUtc);
        document.Commit = new StatisticReconciliationObservationCommit
        {
            SourceDecisionCount = decisionCount,
            MetricPlanEntryCount = metricPlanCount,
            ConfigurationPinCount = configurationCount,
            LineagePinCount = lineageCount,
            AtomCount = atomCount,
            DocumentCount = content.Count + 1,
            ManifestSha256 = manifestSha256,
            MembershipSemanticSha256 = generation.MembershipSemanticSha256
        };
        document.Id = H(
            "P10_EXPECTED_OBSERVATION_COMMIT_ID_V1",
            generation.ContextPin.ReconciliationId,
            generation.GenerationId);
        document.DocumentSemanticSha256 = DocumentHash(
            document,
            I(decisionCount),
            I(metricPlanCount),
            I(configurationCount),
            I(lineageCount),
            I(atomCount),
            I(content.Count + 1),
            manifestSha256,
            generation.MembershipSemanticSha256);
        return document;
    }

    private static StatisticReconciliationObservation Common(
        StatisticReconciliationExpectedCompiledGeneration generation,
        string recordKind,
        DateTime createdAtUtc)
    {
        var context = generation.ContextPin;
        var membership = generation.SourcePlan.BoundInputs.CurrentEpochFlowMembership;
        var catalog = generation.CatalogPins;
        return new StatisticReconciliationObservation
        {
            SchemaVersion = ObservationSchemaVersion,
            RecordKind = recordKind,
            ReconciliationId = context.ReconciliationId,
            ImmutableIdentitySha256 = context.ImmutableIdentitySha256,
            ImmutableHeaderSha256 = context.ImmutableHeaderSha256,
            GenerationId = generation.GenerationId,
            GenerationSemanticSha256 = generation.GenerationSemanticSha256,
            AlgorithmRevision = generation.AlgorithmRevision,
            AlgorithmSha256 = generation.AlgorithmSha256,
            SourceSetSha256 = generation.SourceSetSha256,
            TypedSemanticSha256 = generation.TypedSemanticSha256,
            MetricPlanSha256 = generation.MetricPlanSha256,
            InputBindingSha256 = generation.SourcePlan.BoundInputs
                .InputFingerprints.InputBindingSha256,
            LifecycleSemanticSha256 = generation.SourcePlan.LifecycleSemanticSha256,
            ContributionSemanticSha256 = generation.SourcePlan.ContributionSemanticSha256,
            TenantUnitId = context.TenantUnitId,
            WorkId = context.WorkId,
            ScopeAssignmentId = context.ScopeAssignmentId,
            PeriodKey = context.PeriodKey,
            PeriodInstanceKey = context.PeriodInstanceKey,
            ConceptKey = context.ConceptKey,
            Grain = context.Grain,
            TimeAxis = context.TimeAxis,
            FilterSha256 = context.FilterSha256,
            DynamicFormVersionId = context.DynamicFormVersionId,
            DynamicFormSchemaSha256 = context.DynamicFormSchemaSha256,
            RuntimeKind = membership.RuntimeKind,
            FlowTemplateVersionId = membership.FlowTemplateVersionId,
            FlowPayloadSha256 = membership.FlowPayloadSha256,
            FlowInstanceId = membership.FlowInstanceId,
            ExecutionEpochId = membership.ExecutionEpochId,
            ExecutionEpoch = membership.ExecutionEpoch,
            ExecutionEpochRevision = membership.ExecutionEpochRevision,
            P8ConfigurationOwnerId = context.P8ConfigurationOwnerId,
            P8ConfigurationBundleSha256 = context.P8ConfigurationBundleSha256,
            CatalogPins = new StatisticReconciliationObservationCatalogPins
            {
                P9CatalogVersion = catalog.P9CatalogVersion,
                P9CatalogRawSha256 = catalog.P9CatalogRawSha256,
                P9CatalogSemanticSha256 = catalog.P9CatalogSemanticSha256,
                P9SchemaRawSha256 = catalog.P9SchemaRawSha256,
                P9SchemaSemanticSha256 = catalog.P9SchemaSemanticSha256,
                P9StageLockSha256 = catalog.P9StageLockSha256,
                CandidateChainId = catalog.CandidateChainId,
                CandidatePromptId = catalog.CandidatePromptId,
                CandidateCatalogVersion = catalog.CandidateCatalogVersion,
                CandidateCatalogRawSha256 = catalog.CandidateCatalogRawSha256,
                CandidateCatalogSemanticSha256 = catalog.CandidateCatalogSemanticSha256,
                CandidateSchemaRawSha256 = catalog.CandidateSchemaRawSha256,
                CandidateSchemaSemanticSha256 = catalog.CandidateSchemaSemanticSha256,
                CandidateStageLockSha256 = catalog.CandidateStageLockSha256,
                CatalogPinSetSha256 = catalog.CatalogPinSetSha256
            },
            CreatedAtUtc = createdAtUtc
        };
    }

    private void ValidateGeneration(
        StatisticReconciliationExpectedCompiledGeneration generation)
    {
        if (generation.SchemaVersion !=
                StatisticReconciliationExpectedTypedCompiler.GenerationSchemaVersion ||
            generation.AlgorithmRevision !=
                StatisticReconciliationExpectedTypedCompiler.AlgorithmRevision ||
            generation.AlgorithmSha256 !=
                StatisticReconciliationExpectedTypedCompiler.AlgorithmSha256 ||
            !Equals(generation.ContextPin, generation.SourcePlan.BoundInputs.ContextPin) ||
            generation.SourceSetSha256 != generation.SourcePlan.SourceSetSha256)
            throw Invalid("Generation header is not compiler-bound.");

        try
        {
            _ = StatisticReconciliationExpectedObservationIntegrity
                .RequireGenerationDocumentCount(
                    generation.SourcePlan.SourceDecisions.Length,
                    generation.SourcePlan.BoundInputs.LockedP8Configuration.Pins.Length,
                    generation.SourcePlan.BoundInputs.RuntimeMappingContributionLineage
                        .Pins.Length,
                    generation.MetricPlan.Length,
                    generation.Atoms.Length);
        }
        catch (StatisticReconciliationExpectedObservationIntegrityException error)
        {
            throw Invalid($"Generation document count is invalid: {error.Reason}.");
        }

        ValidateCatalogPins(generation.CatalogPins);
        foreach (var entry in generation.MetricPlan)
            ValidatePlanEntry(entry);
        var planHash =
            StatisticReconciliationExpectedMetricPlanIntegrity.BuildPlanSha256(
                generation.MetricPlan);
        if (planHash != generation.MetricPlanSha256)
            throw Invalid("Metric plan hash mismatch.");

        var previousAtomKey = string.Empty;
        foreach (var atom in generation.Atoms)
        {
            ValidateAtom(atom);
            var key = string.Join(
                "\u001f",
                atom.Identity.IdentitySha256,
                atom.AtomKind,
                atom.ValueIdentitySha256);
            if (string.CompareOrdinal(previousAtomKey, key) >= 0)
                throw Invalid("Atoms must be unique and canonically ordered.");
            previousAtomKey = key;
        }
        var typedHash = H(
            "P10_EXPECTED_TYPED_LEDGER_V1",
            generation.Atoms.Select(item => item.AtomSemanticSha256));
        if (typedHash != generation.TypedSemanticSha256)
            throw Invalid("Typed semantic hash mismatch.");

        var expectedGenerationHash = H(
            StatisticReconciliationExpectedTypedCompiler.GenerationSchemaVersion,
            generation.ContextPin.ReconciliationId,
            generation.SourcePlan.BoundInputs.InputFingerprints.InputBindingSha256,
            generation.SourcePlan.SourceSetSha256,
            generation.MembershipSemanticSha256,
            StatisticReconciliationExpectedTypedCompiler.AlgorithmRevision,
            StatisticReconciliationExpectedTypedCompiler.AlgorithmSha256,
            generation.CatalogPins.CatalogPinSetSha256,
            generation.MetricPlanSha256,
            generation.TypedSemanticSha256);
        var expectedGenerationId = H(
            "P10_EXPECTED_GENERATION_ID_V3",
            generation.ContextPin.ReconciliationId,
            expectedGenerationHash);
        if (generation.GenerationSemanticSha256 != expectedGenerationHash ||
            generation.GenerationId != expectedGenerationId)
            throw Invalid("Generation semantic identity mismatch.");
    }

    private void ValidatePlanEntry(StatisticReconciliationExpectedMetricPlanEntry entry)
    {
        var compiled = identityCompiler.Compile(new ExpectedMetricIdentityRequest(
            entry.Identity.Family,
            entry.Identity.Kind,
            entry.Identity.MetricId,
            entry.Identity.PeriodKey,
            entry.Identity.FieldId,
            entry.Identity.TableId,
            entry.Identity.RowId,
            entry.Identity.LabelId,
            entry.Identity.ScopeKind,
            entry.Identity.ScopeId,
            entry.Identity.Grain,
            entry.Identity.DiffKind));
        if (compiled != entry.Identity)
            throw Invalid("Metric identity hash mismatch.");
        if (entry.SchemaVersion !=
                StatisticReconciliationExpectedMetricPlanEntrySchemaVersions.V2)
            throw Invalid("Metric plan entry schema is invalid.");
        var expected =
            StatisticReconciliationExpectedMetricPlanIntegrity.BuildEntrySha256(
                entry.Identity,
                entry.JsonPointer,
                entry.ValueType,
                entry.Unordered,
                entry.ExpandArray,
                entry.Operations,
                entry.TransitionMode,
                entry.BeforeJsonPointer,
                entry.AfterJsonPointer,
                entry.DifferenceOperation,
                entry.TransitionKind);
        if (expected != entry.PlanEntrySha256)
            throw Invalid("Metric plan entry hash mismatch.");
    }

    private void ValidateAtom(StatisticReconciliationExpectedTypedAtom atom)
    {
        if (atom.SchemaVersion != StatisticReconciliationExpectedTypedCompiler.AtomSchemaVersion)
            throw Invalid("Atom schema is invalid.");
        var expectedCollection = atom.ValueType ==
            StatisticReconciliationExpectedValueTypes.StringList;
        if (expectedCollection != (atom.CollectionSemantics is not null) ||
            atom.CollectionSemantics is not null &&
            !StatisticReconciliationExpectedCollectionSemantics.All.Contains(
                atom.CollectionSemantics))
            throw Invalid("Atom collection semantics mismatch.");
        var valueIdentity = H(
            "P10_EXPECTED_TYPED_VALUE_IDENTITY_V3",
            atom.Identity.IdentitySha256,
            atom.TransitionLeg,
            atom.TransitionKind ?? "~",
            atom.CollectionSemantics ?? "~",
            atom.AtomKind,
            atom.ValueType,
            atom.ValueState,
            atom.CanonicalValue,
            I(atom.DecimalScale));
        var semantic = H(
            StatisticReconciliationExpectedTypedCompiler.AtomSchemaVersion,
            atom.Identity.IdentitySha256,
            atom.TransitionLeg,
            atom.TransitionKind ?? "~",
            atom.CollectionSemantics ?? "~",
            atom.AtomKind,
            atom.ValueType,
            atom.ValueState,
            atom.CanonicalValue,
            I(atom.DecimalScale),
            I(atom.OccurrenceCount),
            I(atom.ReportCount),
            I(atom.RowCount),
            I(atom.NumericValueCount),
            valueIdentity);
        if (valueIdentity != atom.ValueIdentitySha256 ||
            semantic != atom.AtomSemanticSha256)
            throw Invalid("Atom semantic hash mismatch.");
    }
    private static void ValidateCatalogPins(
        StatisticReconciliationExpectedCatalogPins pins)
    {
        var rebuilt = StatisticReconciliationExpectedCatalogPins.Create(
            pins.P9CatalogVersion,
            pins.P9CatalogRawSha256,
            pins.P9CatalogSemanticSha256,
            pins.P9SchemaRawSha256,
            pins.P9SchemaSemanticSha256,
            pins.P9StageLockSha256,
            pins.CandidateChainId,
            pins.CandidatePromptId,
            pins.CandidateCatalogVersion,
            pins.CandidateCatalogRawSha256,
            pins.CandidateCatalogSemanticSha256,
            pins.CandidateSchemaRawSha256,
            pins.CandidateSchemaSemanticSha256,
            pins.CandidateStageLockSha256);
        if (rebuilt != pins)
            throw Invalid("Catalog pin hash mismatch.");
    }

    private static void EnsureUniqueDocuments(
        IReadOnlyList<StatisticReconciliationObservation> documents)
    {
        if (documents.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() !=
            documents.Count)
            throw Invalid("Observation document identities are not unique.");
    }

    private static void ValidateExisting(
        IReadOnlyList<StatisticReconciliationExpectedStoredObservation> existing,
        IReadOnlyDictionary<string, StatisticReconciliationObservation> expected,
        bool allowIncomplete)
    {
        if (existing.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() !=
            existing.Count)
            throw Conflict("Duplicate observation identities were stored.");
        foreach (var item in existing)
        {
            if (!expected.TryGetValue(item.Id, out var expectedItem) ||
                item.RecordKind != expectedItem.RecordKind ||
                item.DocumentSemanticSha256 != expectedItem.DocumentSemanticSha256)
                throw Conflict("Generation contains an unexpected or conflicting observation.");
        }
        if (!allowIncomplete && existing.Count != expected.Count)
            throw Incomplete("Generation commit is missing one or more observations.");
    }

    private static void EnsureAllContentPresent(
        IReadOnlyList<StatisticReconciliationObservation> content,
        IReadOnlyList<StatisticReconciliationExpectedStoredObservation> stored)
    {
        var ids = stored.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (content.Any(item => !ids.Contains(item.Id)))
            throw Incomplete("Content append did not persist the complete generation.");
    }

    private static bool HasCommit(
        IReadOnlyList<StatisticReconciliationExpectedStoredObservation> observations)
        => observations.Any(item =>
            item.RecordKind ==
            StatisticReconciliationObservationRecordKinds.GenerationCommit);

    private static string DocumentHash(
        StatisticReconciliationObservation document,
        params string[] payloadFields)
        => H(
            ObservationSchemaVersion,
            new[]
            {
                document.RecordKind,
                document.ReconciliationId,
                document.ImmutableIdentitySha256,
                document.ImmutableHeaderSha256,
                document.GenerationId,
                document.GenerationSemanticSha256,
                document.AlgorithmRevision,
                document.AlgorithmSha256,
                document.SourceSetSha256,
                document.TypedSemanticSha256,
                document.MetricPlanSha256,
                document.InputBindingSha256,
                document.LifecycleSemanticSha256,
                document.ContributionSemanticSha256,
                document.WorkId,
                document.ScopeAssignmentId,
                document.PeriodKey,
                document.PeriodInstanceKey,
                document.ConceptKey,
                document.Grain,
                document.TimeAxis,
                document.FilterSha256,
                document.DynamicFormVersionId,
                document.DynamicFormSchemaSha256,
                document.RuntimeKind ?? "~",
                document.FlowTemplateVersionId ?? "~",
                document.FlowPayloadSha256 ?? "~",
                document.FlowInstanceId ?? "~",
                document.ExecutionEpochId ?? "~",
                I(document.ExecutionEpoch),
                I(document.ExecutionEpochRevision),
                document.P8ConfigurationOwnerId,
                document.P8ConfigurationBundleSha256,
                document.CatalogPins.CatalogPinSetSha256
            }.Concat(payloadFields));

    private static StatisticReconciliationExpectedObservationAppendResult Result(
        StatisticReconciliationExpectedCompiledGeneration generation,
        string manifestSha256,
        int documentCount,
        bool exactReplay)
        => new(
            generation.ContextPin.ReconciliationId,
            generation.GenerationId,
            generation.GenerationSemanticSha256,
            manifestSha256,
            documentCount,
            exactReplay,
            generation.MetricPlanSha256,
            generation.MetricPlan.Length,
            generation.MembershipSemanticSha256,
            generation.ContextPin.RuntimeKind);

    private static string H(string domain, params string[] values)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(domain, values);

    private static string H(string domain, IEnumerable<string> values)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(domain, values);

    private static string I(long? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "~";

    private static StatisticReconciliationExpectedObservationException Invalid(string message)
        => new(
            StatisticReconciliationExpectedObservationFailureReasons.GenerationInvalid,
            message);

    private static StatisticReconciliationExpectedObservationException Conflict(string message)
        => new(
            StatisticReconciliationExpectedObservationFailureReasons.GenerationConflict,
            message);

    private static StatisticReconciliationExpectedObservationException Incomplete(string message)
        => new(
            StatisticReconciliationExpectedObservationFailureReasons.GenerationIncomplete,
            message);
}
