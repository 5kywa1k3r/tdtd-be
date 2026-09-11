using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using tdtd_be.Models;

using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// Fixed code-owned registry. Persisted plans select only exact owner ids and a
/// registry version; they never persist or accept collection names, filters,
/// projections, regexes, JavaScript, or query operators.
/// </summary>
internal sealed class StatisticReconciliationActualBoundaryRegistry
    : IStatisticReconciliationActualBoundaryRegistry
{
    internal const string Version = "P10_ACTUAL_BOUNDARY_REGISTRY_V1";
    private const int MaxExactIds = 4096;

    public StatisticReconciliationActualMongoBoundaryDescriptor Build(
        StatisticReconciliationActualTrustedCaptureMaterial material,
        ActualSourceMembershipCapture sourceCapture)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (!StringComparer.Ordinal.Equals(
                material.BoundaryRegistryVersion,
                Version))
            throw Fail("VERSION_UNSUPPORTED");
        var run = material.Run ?? throw Fail("RUN_REQUIRED");
        var plan = run.ActualCapturePlan ?? throw Fail("CAPTURE_PLAN_REQUIRED");
        var v4 = StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan);
        var actualConfigurationBundle = Sha(
            run.ActualConfigurationBundleSha256,
            "ACTUAL_CONFIG_BUNDLE");
        ArgumentNullException.ThrowIfNull(sourceCapture);
        var sourceOwners = sourceCapture.Decisions
            .Select(decision => decision.ObservedOwner)
            .OrderBy(owner => owner.OwnerOrdinal)
            .ThenBy(owner => owner.ReportId, StringComparer.Ordinal)
            .ToImmutableArray();
        if (sourceOwners.Length == 0)
            throw Fail("SOURCE_OWNERS_EMPTY");
        var selectors = material.BoundarySelectors ??
                        throw Fail("SELECTORS_REQUIRED");
        var resultGeneration = Required(
            material.Direct.GenerationId,
            "RESULT_GENERATION");
        var observedReportIds = Ids(
            sourceOwners.Select(owner => owner.ReportId).ToImmutableArray(),
            "SOURCE_REPORT_IDS");
        var sourceGeneration = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_SOURCE_BOUNDARY_GENERATION_V1",
            Required(sourceCapture.OwnerGenerationId, "SOURCE_GENERATION_ID"),
            Sha(sourceCapture.OwnerGenerationSha256, "SOURCE_GENERATION_SHA256"),
            StatisticReconciliationActualCanonical.Integer(
                Positive(sourceCapture.OwnerDirectSourceRevision,
                    "SOURCE_DIRECT_REVISION")),
            Sha(sourceCapture.SourceSetSha256, "SOURCE_SET"));
        var resultBoundaryGeneration = v4
            ? StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_RESULT_BOUNDARY_GENERATION_V4",
                resultGeneration,
                Sha(material.Direct.OwnerGenerationSha256,
                    "DIRECT_OWNER_GENERATION"),
                actualConfigurationBundle,
                Sha(plan.Basic.ApplicabilityProofSha256,
                    "BASIC_APPLICABILITY_PROOF"),
                Sha(plan.Advanced.ApplicabilityProofSha256,
                    "ADVANCED_APPLICABILITY_PROOF"),
                Sha(plan.Diff.ApplicabilityProofSha256,
                    "DIFF_APPLICABILITY_PROOF"))
            : StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_RESULT_BOUNDARY_GENERATION_V1",
                resultGeneration,
                Sha(material.Direct.OwnerGenerationSha256,
                    "DIRECT_OWNER_GENERATION"),
                Sha(selectors.BasicImmutableSelectorSha256, "BASIC_SELECTOR"),
                Sha(material.Basic!.Boundary.RequestHash, "BASIC_REQUEST"),
                Sha(material.Basic.Boundary.ConfigSha256, "BASIC_CONFIG"),
                Sha(selectors.AdvancedImmutableSelectorSha256,
                    "ADVANCED_SELECTOR"),
                Sha(material.Advanced!.ConfigSha256, "ADVANCED_CONFIG"),
                IdSetSha(selectors.AdvancedDayNodeIds, "ADVANCED_DAY_IDS"),
                IdSetSha(selectors.AdvancedMonthNodeIds, "ADVANCED_MONTH_IDS"),
                IdSetSha(selectors.AdvancedYearNodeIds, "ADVANCED_YEAR_IDS"),
                Sha(selectors.DiffImmutableSelectorSha256, "DIFF_SELECTOR"),
                Required(material.Diff!.ResultId, "DIFF_RESULT_ID"),
                Required(material.Diff.RunId, "DIFF_RUN_ID"),
                Sha(material.Diff.ConfigSha256, "DIFF_CONFIG"));
        var slices = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualMongoBoundarySlice>();

        AddModelSlices<Work>(
            slices,
            StatisticReconciliationActualBoundaryDomains.Source,
            "works",
            "source-work",
            sourceGeneration,
            Positive(run.SourceLifecycleRevision, "SOURCE_REVISION"),
            IdFilter(run.WorkId));
        AddModelSlices<WorkAssignment>(
            slices,
            StatisticReconciliationActualBoundaryDomains.Source,
            "work_assignments",
            "source-scope-assignment",
            sourceGeneration,
            Positive(run.SourceLifecycleRevision, "SOURCE_REVISION"),
            IdFilter(run.ScopeAssignmentId));

        AddModelSlices<WorkAssignmentReport>(
            slices,
            StatisticReconciliationActualBoundaryDomains.Source,
            "work_assignment_report",
            "source-reports",
            sourceGeneration,
            Positive(run.SourceLifecycleRevision, "SOURCE_REVISION"),
            IdsFilter(observedReportIds, "SOURCE_REPORT_IDS"));

        slices.Add(Slice(
            StatisticReconciliationActualBoundaryDomains.Source,
            "dynamic_flow_mapping_provenance",
            "source-mapping-provenance",
            sourceGeneration,
            Positive(run.SourceLifecycleRevision, "SOURCE_REVISION"),
            new BsonDocument(
                "targetReportId",
                new BsonDocument(
                    "$in",
                    new BsonArray(observedReportIds.Select(value =>
                        ExactObjectId(value, "SOURCE_REPORT_IDS"))))),
            [
                "commandId", "createdAtUtc", "invalidatedAtUtc",
                "invalidatedByEventId", "invalidationReason",
                "mappingRuleSetHash", "ownedTargetRefs", "provenanceHash",
                "provenanceSnapshot", "receiptId", "resultSemanticHash",
                "resultSnapshot", "resultSnapshotHash", "runtimePin",
                "sourcePins", "sourceSignature", "sourceSignatureVersion",
                "state", "supersededByProvenanceId", "supersedesProvenanceId",
                "targetAssignmentId", "targetLifecycleRevision",
                "targetPayloadHash", "targetPayloadRevision", "targetReportId",
                "workId"
            ]));

        var assignmentIds = Ids(
            sourceOwners.Select(owner => owner.WorkAssignmentId).Distinct(
                StringComparer.Ordinal).ToImmutableArray(),
            "SOURCE_ASSIGNMENT_IDS");
        AddModelSlices<WorkAssignment>(
            slices,
            StatisticReconciliationActualBoundaryDomains.Source,
            "work_assignments",
            "source-assignments",
            sourceGeneration,
            Positive(run.SourceLifecycleRevision, "SOURCE_REVISION"),
            IdsFilter(assignmentIds, "SOURCE_ASSIGNMENT_IDS"));

        var payloadIds = Ids(
            sourceOwners.Select(owner => owner.PayloadDocumentId).Distinct(
                StringComparer.Ordinal).ToImmutableArray(),
            "SOURCE_PAYLOAD_IDS");
        slices.Add(Slice(
            StatisticReconciliationActualBoundaryDomains.Source,
            "work_report_payloads",
            "source-payloads",
            sourceGeneration,
            Positive(sourceOwners.Max(owner => owner.PayloadRevision),
                "SOURCE_PAYLOAD_REVISION"),
            IdsFilter(payloadIds, "SOURCE_PAYLOAD_IDS"),
            Projection<WorkReportPayload>()));

        AddModelSlices<WorkReportPeriod>(
            slices,
            StatisticReconciliationActualBoundaryDomains.Source,
            "work_report_periods",
            "source-periods",
            sourceGeneration,
            Positive(run.SourceLifecycleRevision, "SOURCE_REVISION"),
            new BsonDocument
            {
                ["workId"] = ExactObjectId(run.WorkId, "WORK_ID"),
                ["workAssignmentId"] = new BsonDocument(
                    "$in",
                    new BsonArray(assignmentIds.Select(value =>
                        ExactObjectId(value, "SOURCE_ASSIGNMENT_IDS")))),
                ["periodInstanceKey"] = Required(
                    run.PeriodInstanceKey,
                    "PERIOD_INSTANCE_KEY"),
                ["isDeleted"] = false
            });

        AddModelSlices<DynamicFlowTemplate>(
            slices,
            StatisticReconciliationActualBoundaryDomains.Source,
            "dynamic_flow_templates",
            "source-flow-template",
            sourceGeneration,
            Positive(run.FlowFamilyRevision, "FLOW_FAMILY_REVISION"),
            IdFilter(Required(run.FlowTemplateId, "FLOW_TEMPLATE_ID")));
        AddModelSlices<DynamicFlowTemplateVersion>(
            slices,
            StatisticReconciliationActualBoundaryDomains.Source,
            "dynamic_flow_template_versions",
            "source-flow-template-version",
            sourceGeneration,
            Positive(run.FlowExecutionEpochRevision, "FLOW_EPOCH_REVISION"),
            IdFilter(Required(
                run.FlowTemplateVersionId,
                "FLOW_TEMPLATE_VERSION_ID")));

        var templateFilter = IdFilter(
            Required(run.DynamicFormVersionId, "FORM_VERSION_ID"));
        AddModelSlices<DynamicFormTemplate>(
            slices,
            StatisticReconciliationActualBoundaryDomains.Configuration,
            "dynamic_form_templates",
            "dynamic-form-version",
            actualConfigurationBundle,
            NonNegative(run.P8ConfigRevision, "P8_CONFIG_REVISION"),
            templateFilter);
        if (material.Basic is not null)
        {
            slices.Add(Slice(
                StatisticReconciliationActualBoundaryDomains.Configuration,
                "work_assignment_basic_summary_configs",
                "basic-config",
                actualConfigurationBundle,
                Positive(material.Basic.Boundary.ConfigRevision,
                    "BASIC_CONFIG_REVISION"),
                IdFilter(material.Basic.Boundary.ConfigId),
                Projection<WorkAssignmentBasicSummaryConfig>()));
        }
        if (material.Advanced is not null)
        {
            slices.Add(Slice(
                StatisticReconciliationActualBoundaryDomains.Configuration,
                "work_assignment_advanced_summary_configs",
                "advanced-config",
                actualConfigurationBundle,
                Positive(material.Advanced.ConfigRevision,
                    "ADVANCED_CONFIG_REVISION"),
                IdFilter(material.Advanced.ConfigVersionId),
                Projection<WorkAssignmentAdvancedSummaryConfig>()));
        }
        if (material.Diff is not null)
        {
            slices.Add(Slice(
                StatisticReconciliationActualBoundaryDomains.Configuration,
                "work_report_statistic_diff_configs",
                "diff-config",
                actualConfigurationBundle,
                Positive(material.Diff.ConfigRevision, "DIFF_CONFIG_REVISION"),
                IdFilter(material.Diff.ConfigVersionId),
                Projection<WorkReportStatisticDiffConfig>()));
        }
        AddModelSlices<DynamicFormTemplate>(
            slices,
            StatisticReconciliationActualBoundaryDomains.Catalog,
            "dynamic_form_templates",
            "dynamic-form-catalog",
            Sha(run.P9StageLockSha256, "P9_STAGE_LOCK"),
            NonNegative(run.CandidateStage, "CANDIDATE_STAGE"),
            templateFilter);

        if (StringComparer.Ordinal.Equals(
                material.Api.Surface,
                StatisticReconciliationActualApiSurfaces.DirectLabel))
        {
            AddModelSlices<LabelCatalogItem>(
                slices,
                StatisticReconciliationActualBoundaryDomains.Catalog,
                "labels",
                "api-label-catalog",
                Sha(run.P9StageLockSha256, "P9_STAGE_LOCK"),
                NonNegative(run.CandidateStage, "CANDIDATE_STAGE"),
                new BsonDocument
                {
                    ["isActive"] = true,
                    ["isDeleted"] = false
                });
        }

        AddModelSlices<DynamicFlowInstance>(
            slices,
            StatisticReconciliationActualBoundaryDomains.Runtime,
            "dynamic_flow_instances",
            "flow-instance",
            Required(run.FlowExecutionEpochId, "FLOW_EPOCH_ID"),
            Positive(run.FlowExecutionEpochRevision, "FLOW_EPOCH_REVISION"),
            IdFilter(Required(run.FlowInstanceId, "FLOW_INSTANCE_ID")));

        AddModelSlices<DynamicFlowExecutionEpoch>(
            slices,
            StatisticReconciliationActualBoundaryDomains.Runtime,
            "dynamic_flow_execution_epochs",
            "flow-execution-epoch",
            Required(run.FlowExecutionEpochId, "FLOW_EPOCH_ID"),
            Positive(run.FlowExecutionEpochRevision, "FLOW_EPOCH_REVISION"),
            IdFilter(Required(run.FlowExecutionEpochId, "FLOW_EPOCH_ID")));

        AddModelSlices<DynamicFlowStepInstance>(
            slices,
            StatisticReconciliationActualBoundaryDomains.Runtime,
            "dynamic_flow_step_instances",
            "flow-step-instance",
            Required(run.FlowExecutionEpochId, "FLOW_EPOCH_ID"),
            Positive(run.FlowStepInstanceRevision, "FLOW_STEP_REVISION"),
            IdFilter(Required(run.FlowStepInstanceId, "FLOW_STEP_INSTANCE_ID")));

        AddModelSlices<WorkReportStatisticRebuildJob>(
            slices,
            StatisticReconciliationActualBoundaryDomains.Result,
            "work_report_statistic_rebuild_jobs",
            "direct-publication",
            resultBoundaryGeneration,
            Positive(
                material.Aggregate.DirectPublicationRevision,
                "DIRECT_PUBLICATION_REVISION"),
            IdFilter(Required(run.P9RunId, "P9_RUN_ID")));

        foreach (var collection in new[]
                 {
                     "work_report_field_stat_values",
                     "work_report_table_stat_values",
                     "work_report_label_stat_values",
                     "work_report_field_stat_aggregates",
                     "work_report_table_stat_aggregates",
                     "work_report_label_stat_aggregates"
                 })
        {
            slices.Add(Slice(
                StatisticReconciliationActualBoundaryDomains.Result,
                collection,
                collection,
                resultBoundaryGeneration,
                Positive(
                    material.Aggregate.DirectPublicationRevision,
                    "DIRECT_PUBLICATION_REVISION"),
                new BsonDocument
                {
                    ["directProjection.generationId"] = resultGeneration,
                    ["isDeleted"] = false
                },
                ResultProjection(collection)));
        }

        if (material.Basic is not null)
        {
            slices.Add(Slice(
                StatisticReconciliationActualBoundaryDomains.Result,
                "work_assignment_basic_summary_snapshots",
                "basic-snapshot",
                resultBoundaryGeneration,
                Positive(material.Basic.Boundary.ConfigRevision,
                    "BASIC_REVISION"),
                IdFilter(Required(selectors.BasicSnapshotId,
                    "BASIC_SNAPSHOT_ID")),
                Projection<WorkAssignmentBasicSummarySnapshot>()));
        }
        if (material.Advanced is not null)
        {
            AddAdvanced(
                slices,
                "work_assignment_advanced_summary_day_nodes",
                "advanced-day",
                selectors.AdvancedDayNodeIds,
                resultBoundaryGeneration,
                material.Advanced.ConfigRevision);
            AddAdvanced(
                slices,
                "work_assignment_advanced_summary_month_nodes",
                "advanced-month",
                selectors.AdvancedMonthNodeIds,
                resultBoundaryGeneration,
                material.Advanced.ConfigRevision);
            AddAdvanced(
                slices,
                "work_assignment_advanced_summary_year_nodes",
                "advanced-year",
                selectors.AdvancedYearNodeIds,
                resultBoundaryGeneration,
                material.Advanced.ConfigRevision);
            AddAdvancedTopology<WorkAssignmentAdvancedSummaryDayNode>(
                slices,
                "work_assignment_advanced_summary_day_nodes",
                "advanced-day-topology",
                material,
                resultBoundaryGeneration);
            AddAdvancedTopology<WorkAssignmentAdvancedSummaryMonthNode>(
                slices,
                "work_assignment_advanced_summary_month_nodes",
                "advanced-month-topology",
                material,
                resultBoundaryGeneration);
            AddAdvancedTopology<WorkAssignmentAdvancedSummaryYearNode>(
                slices,
                "work_assignment_advanced_summary_year_nodes",
                "advanced-year-topology",
                material,
                resultBoundaryGeneration);
        }
        if (material.Diff is not null)
        {
            slices.Add(Slice(
                StatisticReconciliationActualBoundaryDomains.Result,
                "work_report_statistic_diff_results",
                "diff-result",
                resultBoundaryGeneration,
                Positive(material.Diff.ConfigRevision, "DIFF_REVISION"),
                IdFilter(Required(selectors.DiffResultId, "DIFF_RESULT_ID")),
                Projection<WorkReportStatisticDiffResult>()));
        }

        if (!StringComparer.Ordinal.Equals(
                selectors.ExportCollection,
                "work_report_statistic_exports") &&
            !StringComparer.Ordinal.Equals(
                selectors.ExportCollection,
                "work_report_statistic_diff_exports"))
        {
            throw Fail("EXPORT_COLLECTION_INVALID");
        }
        AddModelSlices<StatRunExportArtifact>(
            slices,
            StatisticReconciliationActualBoundaryDomains.Export,
            selectors.ExportCollection,
            "export-artifact",
            Sha(
                material.Export.ExpectedOwnerSemanticSha256,
                "EXPORT_OWNER_SEMANTIC"),
            NonNegative(
                selectors.ExportLifecycleRevision,
                "EXPORT_LIFECYCLE_REVISION"),
            StringIdFilter(selectors.ExportId));

        var immutableSlices = slices.ToImmutable();
        if (immutableSlices.Length > 64)
            throw Fail("SLICE_LIMIT_EXCEEDED");

        return new StatisticReconciliationActualMongoBoundaryDescriptor(
            Required(run.Id, "RUN_ID"),
            Required(run.WorkId, "WORK_ID"),
            Required(run.ScopeAssignmentId, "SCOPE_ASSIGNMENT_ID"),
            Sha(sourceCapture.SourceSetSha256, "SOURCE_SET"),
            actualConfigurationBundle,
            Sha(run.FilterHash, "FILTER"),
            Sha(run.AuthorizationSnapshotHash, "RUN_AUTHORIZATION"),
            immutableSlices);
    }

    private static void AddAdvancedTopology<TNode>(
        ImmutableArray<StatisticReconciliationActualMongoBoundarySlice>.Builder
            slices,
        string collection,
        string ownerKey,
        StatisticReconciliationActualTrustedCaptureMaterial material,
        string ownerGeneration)
        where TNode : WorkAssignmentAdvancedSummaryHierarchyNodeBase
    {
        var run = material.Run ?? throw Fail("RUN_REQUIRED");
        var periodStart = Utc(run.PeriodStartUtc, "PERIOD_START_UTC");
        var periodEnd = Utc(run.PeriodEndUtc, "PERIOD_END_UTC");
        if (periodEnd <= periodStart)
            throw Fail("PERIOD_WINDOW_INVALID");
        AddModelSlices<TNode>(
            slices,
            StatisticReconciliationActualBoundaryDomains.Result,
            collection,
            ownerKey,
            ownerGeneration,
            Positive(material.Advanced!.ConfigRevision, "ADVANCED_REVISION"),
            new BsonDocument
            {
                ["workId"] = ExactObjectId(run.WorkId, "WORK_ID"),
                ["assignmentId"] = ExactObjectId(
                    run.ScopeAssignmentId,
                    "SCOPE_ASSIGNMENT_ID"),
                ["dynamicFormTemplateId"] = ExactObjectId(
                    run.DynamicFormVersionId,
                    "FORM_VERSION_ID"),
                ["sectionId"] = Required(
                    material.Advanced!.SectionId,
                    "ADVANCED_SECTION_ID"),
                ["isDeleted"] = false,
                ["windowStartUtc"] = new BsonDocument(
                    "$lte",
                    new BsonDateTime(periodEnd)),
                ["windowEndExclusiveUtc"] = new BsonDocument(
                    "$gt",
                    new BsonDateTime(periodStart))
            });
    }

    private static void AddModelSlices<T>(
        ImmutableArray<StatisticReconciliationActualMongoBoundarySlice>.Builder
            slices,
        string domain,
        string collection,
        string ownerKey,
        string generationKey,
        long revision,
        BsonDocument filter)
    {
        var chunks = ProjectionChunks<T>();
        for (var index = 0; index < chunks.Length; index++)
        {
            slices.Add(Slice(
                domain,
                collection,
                $"{ownerKey}-{index:D2}",
                generationKey,
                revision,
                filter,
                chunks[index]));
        }
    }
    private static void AddAdvanced(
        ImmutableArray<StatisticReconciliationActualMongoBoundarySlice>.Builder
            slices,
        string collection,
        string ownerKey,
        ImmutableArray<string> ids,
        string ownerGeneration,
        long revision)
    {
        var normalized = Ids(ids, ownerKey);
        if (normalized.Length == 0)
            return;
        slices.Add(Slice(
            StatisticReconciliationActualBoundaryDomains.Result,
            collection,
            ownerKey,
            ownerGeneration,
            Positive(revision, "ADVANCED_REVISION"),
            IdsFilter(normalized, ownerKey),
            AdvancedProjection(collection)));
    }

    private static ImmutableArray<string> ResultProjection(string collection)
        => collection switch
        {
            "work_report_field_stat_values" =>
                Projection<WorkReportFieldStatValue>(),
            "work_report_table_stat_values" =>
                Projection<WorkReportTableStatValue>(),
            "work_report_label_stat_values" =>
                Projection<WorkReportLabelStatValue>(),
            "work_report_field_stat_aggregates" =>
                Projection<WorkReportFieldStatAggregate>(),
            "work_report_table_stat_aggregates" =>
                Projection<WorkReportTableStatAggregate>(),
            "work_report_label_stat_aggregates" =>
                Projection<WorkReportLabelStatAggregate>(),
            _ => throw Fail("RESULT_COLLECTION_INVALID")
        };

    private static ImmutableArray<string> AdvancedProjection(string collection)
        => collection switch
        {
            "work_assignment_advanced_summary_day_nodes" =>
                Projection<WorkAssignmentAdvancedSummaryDayNode>(),
            "work_assignment_advanced_summary_month_nodes" =>
                Projection<WorkAssignmentAdvancedSummaryMonthNode>(),
            "work_assignment_advanced_summary_year_nodes" =>
                Projection<WorkAssignmentAdvancedSummaryYearNode>(),
            _ => throw Fail("ADVANCED_COLLECTION_INVALID")
        };

    private static ImmutableArray<string> Projection<T>()
    {
        var fields = ModelProjectionFields<T>();
        if (fields.Length > 64)
            throw Fail("MODEL_PROJECTION_BOUNDS_INVALID");
        return fields;
    }

    private static ImmutableArray<ImmutableArray<string>> ProjectionChunks<T>()
    {
        var fields = ModelProjectionFields<T>();
        var chunks = ImmutableArray.CreateBuilder<ImmutableArray<string>>(
            checked((fields.Length + 63) / 64));
        for (var offset = 0; offset < fields.Length; offset += 64)
        {
            chunks.Add(fields
                .Skip(offset)
                .Take(Math.Min(64, fields.Length - offset))
                .ToImmutableArray());
        }
        return chunks.MoveToImmutable();
    }

    private static ImmutableArray<string> ModelProjectionFields<T>()
    {
        var fields = BsonClassMap.LookupClassMap(typeof(T)).AllMemberMaps
            .Select(member => member.ElementName)
            .Where(name => !StringComparer.Ordinal.Equals(name, "_id"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToImmutableArray();
        if (fields.Length == 0 || fields.Length > 4096)
            throw Fail("MODEL_PROJECTION_BOUNDS_INVALID");
        return fields;
    }

    private static StatisticReconciliationActualMongoBoundarySlice Slice(
        string domain,
        string collection,
        string ownerKey,
        string generationKey,
        long revision,
        BsonDocument filter,
        ImmutableArray<string> projections)
        => new(
            domain,
            collection,
            ownerKey,
            generationKey,
            revision,
            filter,
            projections);

    private static BsonDocument IdFilter(string id)
        => new("_id", ExactObjectId(id, "OBJECT_ID"));

    private static BsonDocument StringIdFilter(string id)
        => new("_id", Required(id, "STRING_ID"));

    private static BsonDocument IdsFilter(
        ImmutableArray<string> values,
        string name)
    {
        var ids = Ids(values, name);
        if (ids.Length == 0)
            throw Fail($"{name}_EMPTY");
        return new BsonDocument(
            "_id",
            new BsonDocument(
                "$in",
                new BsonArray(ids.Select(value => ExactObjectId(value, name)))));
    }

    private static ImmutableArray<string> Ids(
        ImmutableArray<string> values,
        string name)
    {
        if (values.IsDefault || values.Length > MaxExactIds)
            throw Fail($"{name}_INVALID");
        var result = values
            .Select(value => ExactObjectId(value, name).ToString())
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw Fail($"{name}_DUPLICATE");
        return result;
    }

    private static string IdSetSha(
        ImmutableArray<string> values,
        string name)
        => StatisticReconciliationActualCanonical.HashSequence(
            $"P10_ACTUAL_{name}_V1",
            Ids(values, name));

    private static ObjectId ExactObjectId(string? value, string name)
    {
        value = Required(value, name);
        if (!ObjectId.TryParse(value, out var parsed) ||
            !StringComparer.Ordinal.Equals(value, parsed.ToString()))
            throw Fail($"{name}_NON_CANONICAL_OBJECT_ID");
        return parsed;
    }

    private static DateTime Utc(DateTime? value, string name)
        => value is { Kind: DateTimeKind.Utc } utc
            ? utc
            : throw Fail($"{name}_INVALID");
    private static int NonNegative(int value, string name)
        => value >= 0 ? value : throw Fail($"{name}_INVALID");

    private static long NonNegative(long? value, string name)
        => value is >= 0 ? value.Value : throw Fail($"{name}_INVALID");

    private static long Positive(long value, string name)
        => value > 0 ? value : throw Fail($"{name}_INVALID");

    private static long Positive(long? value, string name)
        => value is > 0 ? value.Value : throw Fail($"{name}_INVALID");

    private static int Positive(int value, string name)
        => value > 0 ? value : throw Fail($"{name}_INVALID");

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);

    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_BOUNDARY_REGISTRY_{reason}");
}
