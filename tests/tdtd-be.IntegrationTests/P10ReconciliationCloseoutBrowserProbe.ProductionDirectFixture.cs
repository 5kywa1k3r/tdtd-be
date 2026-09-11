using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    private const string P10ProductionDirectSectionId =
        "p10-closeout-direct-section";
    private const string P10ProductionDirectFieldId = "field_amount";
    private const string P10ProductionDirectFieldKey = "amount";
    private const string P10ProductionDirectSecondFieldId =
        "field_amount_secondary";
    private const string P10ProductionDirectSecondFieldKey =
        "amount_secondary";
    private const string P10ProductionLifecycleJobCollection =
        "work_report_statistic_rebuild_jobs";

    private static readonly string P10ProductionDirectSectionsJson =
        new JsonArray
        {
            new JsonObject
            {
                ["id"] = P10ProductionDirectSectionId,
                ["title"] = "P10 closeout DIRECT fixture",
                ["description"] = null,
                ["tagCodes"] = new JsonArray(),
                ["order"] = 0
            }
        }.ToJsonString();

    private static readonly string P10ProductionDirectFieldsJson =
        new JsonArray
        {
            new JsonObject
            {
                ["id"] = P10ProductionDirectFieldId,
                ["sectionId"] = P10ProductionDirectSectionId,
                ["key"] = P10ProductionDirectFieldKey,
                ["name"] = "P10 closeout amount",

                ["type"] = "number",
                ["isStatistic"] = true,
                ["statisticLabelCodes"] = new JsonArray { P10ProductionDirectFieldKey },
                ["statistic"] = new JsonObject
                {
                    ["aggregateOps"] = new JsonArray
                    {
                        "COUNT", "SUM", "AVG", "MIN", "MAX"
                    },
                    ["bucketMode"] = "NONE",
                    ["showInTree"] = true,
                    ["showInDetail"] = true
                }
            }
        }.ToJsonString();

    private static readonly string P10ProductionDirectTwoFieldsJson =
        new JsonArray
        {
            new JsonObject
            {
                ["id"] = P10ProductionDirectFieldId,
                ["sectionId"] = P10ProductionDirectSectionId,
                ["key"] = P10ProductionDirectFieldKey,
                ["name"] = "P10 closeout amount",
                ["type"] = "number",
                ["isStatistic"] = true,
                ["statisticLabelCodes"] = new JsonArray
                {
                    P10ProductionDirectFieldKey
                },
                ["statistic"] = new JsonObject
                {
                    ["aggregateOps"] = new JsonArray
                    {
                        "COUNT", "SUM"
                    },
                    ["bucketMode"] = "NONE",
                    ["showInTree"] = true,
                    ["showInDetail"] = true
                }
            },
            new JsonObject
            {
                ["id"] = P10ProductionDirectSecondFieldId,
                ["sectionId"] = P10ProductionDirectSectionId,
                ["key"] = P10ProductionDirectSecondFieldKey,
                ["name"] = "P10 closeout secondary amount",
                ["type"] = "number",
                ["isStatistic"] = true,
                ["statisticLabelCodes"] = new JsonArray
                {
                    P10ProductionDirectSecondFieldKey
                },
                ["statistic"] = new JsonObject
                {
                    ["aggregateOps"] = new JsonArray
                    {
                        "COUNT", "SUM"
                    },
                    ["bucketMode"] = "NONE",
                    ["showInTree"] = true,
                    ["showInDetail"] = true
                }
            }
        }.ToJsonString();

    /// <summary>
    /// Builds one honest production P9 lifecycle publication for the P10 browser
    /// MATCHED fixture. This method deliberately owns only form/config preparation,
    /// draft persistence, canonical P7 mapping, lifecycle submit/approve, and the
    /// lifecycle projection worker. BASIC/ADVANCED/DIFF/export/capture-plan owners
    /// belong to the caller.
    /// </summary>
    private async Task<P10ProductionDirectFixturePins>
        PrepareProductionDirectLifecycleFixtureAsync(
            CancellationToken ct,
            bool pinMappingToEffectiveVersion = false,
            bool markMappingReceiptReconciled = false,
            bool useNativeNullPeriodPair = false,
            string? cumulativeContributionPolicyJson = null,
            bool includeSecondStatisticField = false)
    {
        var ownedCleanup = new List<P10CleanupHandle>();
        var fixture = Fixture();
        await RetireP10SyntheticSeedPublicationAsync(fixture, ct);
        var schema = await PrepareProductionDirectSchemaAndConfigAsync(
            fixture,
            ownedCleanup,
            useNativeNullPeriodPair,
            includeSecondStatisticField,
            ct);

        if (includeSecondStatisticField)
        {
            var reportShapeWrite = await RequireDatabase()
                .GetCollection<WorkAssignmentReport>(
                    "work_assignment_report")
                .UpdateOneAsync(
                    report =>
                        report.Id == fixture.ReportId &&
                        !report.IsDeleted,
                    Builders<WorkAssignmentReport>.Update
                        .Set(report => report.DataRectR0, 0)
                        .Set(report => report.DataRectC0, 0)
                        .Set(report => report.DataRectR1, 0)
                        .Set(report => report.DataRectC1, 1)
                        .Set(report => report.W, 2)
                        .Set(report => report.H, 1),
                    cancellationToken: ct);
            HarnessAssert.Equal(
                1L,
                reportShapeWrite.MatchedCount,
                "P10 production DIRECT two-field report input shape");
        }

        fixture = fixture with
        {
            ConfigId = schema.ConfigId,
            ConfigVersionId = schema.ConfigVersionId,
            ConfigRevision = schema.ConfigRevision,
            ConfigHash = schema.ConfigHash,
            ConfigBundleHash = schema.ConfigBundleHash,
            FlowPayloadHash = schema.FlowPayloadHash,
            ConceptKey = P10ProductionDirectFieldKey,
            Grain = "MONTH"
        };
        _fixture = fixture;

        await RebuildP10ProductionAssignmentReadModelAsync(
            fixture,
            ownedCleanup,
            ct);

        var draftValues = includeSecondStatisticField
            ? new JsonArray { 10, 20 }
            : new JsonArray { 10 };
        var draftFieldValues = new JsonObject
        {
            ["values"] = new JsonObject
            {
                [P10ProductionDirectFieldKey] = 10
            }
        };
        if (includeSecondStatisticField)
        {
            draftFieldValues["values"]!
                .AsObject()[P10ProductionDirectSecondFieldKey] = 20;
        }

        var draft = await RequireApi().PutAsync(
            $"api/work-assignment-reports/{fixture.ReportId}/draft",
            new JsonObject
            {
                ["expectedPayloadRevision"] = 1,
                ["commandId"] = "p10-closeout-direct-draft-001",
                ["values1D"] = draftValues,
                ["fieldValuesJson"] = draftFieldValues.ToJsonString(),
                ["tableValuesJson"] = "{\"blocks\":[]}",
                ["dataOrigin"] = "MANUAL",
                ["cumulativeContributionMode"] =
                    DynamicFlowContributionPolicyContract.Include,
                ["summarySourceJson"] = "{}",
                ["note"] = "P10 closeout production DIRECT fixture"
            },
            Actor("executor").Token,
            ct: ct);
        P10ProductionExpectSuccess(draft, "P10 production DIRECT draft save");

        var savedPayload = await RequireDatabase()
            .GetCollection<WorkReportPayload>("work_report_payloads")
            .Find(payload =>
                payload.ReportId == fixture.ReportId &&
                !payload.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.Equal(
            includeSecondStatisticField ? "[10,20]" : "[10]",
            savedPayload.Values1DJson,
            "P10 production DIRECT Values1D payload");
        HarnessAssert.Equal(
            includeSecondStatisticField
                ? "{\"values\":{\"field_amount\":10,\"field_amount_secondary\":20}}"
                : "{\"values\":{\"field_amount\":10}}",
            savedPayload.FieldValuesJson,
            "P10 production DIRECT scalar field payload");

        var mapping = await SeedP10CanonicalP7DirectMappingLineageAsync(
            fixture,
            ownedCleanup,
            pinMappingToEffectiveVersion
                ? schema.FlowEffectiveVersionId
                : null,
            cumulativeContributionPolicyJson,
            ct);
        if (markMappingReceiptReconciled)
        {
            var reconciledAtUtc = DateTime.UtcNow;
            var receiptWrite = await RequireDatabase()
                .GetCollection<DynamicFlowMappingApplyReceipt>(
                    "dynamic_flow_mapping_apply_receipts")
                .UpdateOneAsync(
                    receipt =>
                        receipt.Id == mapping.ReceiptId &&
                        receipt.State == DynamicFlowMappingApplyStates.Committed,
                    Builders<DynamicFlowMappingApplyReceipt>.Update
                        .Set(
                            receipt => receipt.State,
                            DynamicFlowMappingApplyStates.Reconciled)
                        .Set(
                            receipt => receipt.ReconciledAtUtc,
                            reconciledAtUtc)
                        .Set(receipt => receipt.UpdatedAtUtc, reconciledAtUtc)
                        .Unset(receipt => receipt.ErrorCode),
                    cancellationToken: ct);
            HarnessAssert.Equal(
                1L,
                receiptWrite.ModifiedCount,
                "P10 production DIRECT mapping receipt RECONCILED transition");
        }

        var report = await LoadP10ProductionReportAsync(fixture.ReportId, ct);
        if (useNativeNullPeriodPair)
        {
            RequireP10ProductionNullPeriodPair(
                report,
                "P10 production DIRECT pre-submit report");
        }
        var submitCommandId = "p10-closeout-direct-submit-001";
        var submit = await RequireApi().PostAsync(
            $"api/work-assignment-reports/{fixture.ReportId}/submit",
            new JsonObject
            {
                ["expectedPayloadRevision"] =
                    report.GetValue("payloadRevision").ToInt32(),
                ["expectedLifecycleRevision"] =
                    report.GetValue("lifecycleRevision").ToInt32(),
                ["commandId"] = submitCommandId
            },
            Actor("executor").Token,
            ct: ct);
        P10ProductionExpectSuccess(submit, "P10 production DIRECT submit");
        await ProcessP10ProductionLifecycleOutboxAsync(ct);
        await RequireP10ProductionLifecycleEntryAsync(
            fixture.ReportId,
            "SUBMIT",
            submitCommandId,
            expectedState: "COMPLETED",
            ct);

        var executor = Actor("executor");
        var executor2 = Actor("executor2");
        var insufficient = Actor("insufficient");
        var admin = Actor("admin");
        var assignments = RequireDatabase()
            .GetCollection<WorkAssignment>("work_assignments");
        var assignmentBeforeOwner = await assignments
            .Find(assignment =>
                assignment.Id == fixture.ScopeAssignmentId &&
                assignment.WorkId == fixture.WorkId &&
                assignment.IsActive &&
                !assignment.IsDeleted)
            .SingleAsync(ct);
        var leaderWatcher = assignmentBeforeOwner.Assignees
            .Single(user => user.UserId == executor.Id);
        HarnessAssert.True(
            assignmentBeforeOwner.CreatedByUserId == executor.Id &&
            assignmentBeforeOwner.LeaderWatcherUserIds.Count == 2 &&
            assignmentBeforeOwner.LeaderWatcherUserIds.Contains(
                executor2.Id,
                StringComparer.Ordinal) &&
            assignmentBeforeOwner.LeaderWatcherUserIds.Contains(
                insufficient.Id,
                StringComparer.Ordinal) &&
            assignmentBeforeOwner.LeaderWatchers.Count == 0 &&
            leaderWatcher.UnitId == executor.UnitId &&
            (assignmentBeforeOwner.TargetUnitIds?.Contains(
                executor.UnitId,
                StringComparer.Ordinal) ?? false),
            "P10 production DIRECT review owner precondition drifted.");

        var af = Builders<WorkAssignment>.Filter;
        var assignmentOwner = await assignments.UpdateOneAsync(
            af.Eq(assignment => assignment.Id, fixture.ScopeAssignmentId) &
            af.Eq(assignment => assignment.WorkId, fixture.WorkId) &
            af.Eq(assignment => assignment.CreatedByUserId, executor.Id) &
            af.Eq(assignment => assignment.IsActive, true) &
            af.Eq(assignment => assignment.IsDeleted, false) &
            af.Size(assignment => assignment.LeaderWatcherUserIds, 2) &
            af.All(
                assignment => assignment.LeaderWatcherUserIds,
                new[] { executor2.Id, insufficient.Id }) &
            af.Size(assignment => assignment.LeaderWatchers, 0),
            Builders<WorkAssignment>.Update
                .Set(assignment => assignment.CreatedByUserId, admin.Id)
                .Set(
                    assignment => assignment.LeaderWatcherUserIds,
                    new List<string>
                    {
                        executor2.Id,
                        insufficient.Id,
                        executor.Id
                    })
                .Set(
                    assignment => assignment.LeaderWatchers,
                    new List<UserRef> { leaderWatcher }),
            cancellationToken: ct);
        HarnessAssert.Equal(
            1L,
            assignmentOwner.MatchedCount,
            "P10 production DIRECT review owner assignment");
        HarnessAssert.Equal(
            1L,
            assignmentOwner.ModifiedCount,
            "P10 production DIRECT review owner assignment write");
        await RebuildP10ProductionAssignmentReadModelAsync(
            fixture,
            ownedCleanup,
            ct);

        report = await LoadP10ProductionReportAsync(fixture.ReportId, ct);
        if (useNativeNullPeriodPair)
        {
            RequireP10ProductionNullPeriodPair(
                report,
                "P10 production DIRECT pre-approve report");
        }
        var approveCommandId = "p10-closeout-direct-approve-001";
        var approve = await RequireApi().PostAsync(
            $"api/work-assignment-review/reports/{fixture.ReportId}/approve",
            new JsonObject
            {
                ["expectedPayloadRevision"] =
                    report.GetValue("payloadRevision").ToInt32(),
                ["expectedLifecycleRevision"] =
                    report.GetValue("lifecycleRevision").ToInt32(),
                ["commandId"] = approveCommandId,
                ["comment"] = "P10 closeout production DIRECT approval"
            },
            Actor("admin").Token,
            ct: ct);
        P10ProductionExpectSuccess(approve, "P10 production DIRECT approve");

        var approveEntry = await DrainP10ProductionApprovePublicationAsync(
            fixture.ReportId,
            approveCommandId,
            ct);
        var runId = P10ProductionRequiredBsonString(
            approveEntry,
            "directProjectionRunId");
        var generationId = P10ProductionRequiredBsonString(
            approveEntry,
            "directProjectionGenerationId");
        var generationHash = P10ProductionRequiredBsonString(
            approveEntry,
            "directProjectionGenerationHash");
        HarnessAssert.True(
            ObjectId.TryParse(runId, out _),
            "P10 production DIRECT run id must be an ObjectId.");
        HarnessAssert.True(
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                generationId),
            "P10 production DIRECT generation id must be SHA-256.");
        HarnessAssert.True(
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                generationHash),
            "P10 production DIRECT generation hash must be SHA-256.");

        var job = await RequireDatabase()
            .GetCollection<WorkReportStatisticRebuildJob>(
                P10ProductionLifecycleJobCollection)
            .Find(item => item.Id == runId && !item.IsDeleted)
            .SingleAsync(ct);
        RequireP10ProductionPublishedJob(
            job,
            fixture,
            schema,
            mapping,
            pinMappingToEffectiveVersion
                ? schema.FlowEffectiveVersionId
                : schema.FlowOriginVersionId,
            useNativeNullPeriodPair);
        await RequireP10ProductionUniqueCurrentPublicationAsync(
            job,
            fixture,
            ct);

        RegisterP10ProductionCleanup(
            ownedCleanup,
            P10ProductionLifecycleJobCollection,
            runId);
        await RegisterP10ProductionDirectRowsAsync(
            generationId,
            ownedCleanup,
            ct);

        var finalReport = await LoadP10ProductionReportAsync(
            fixture.ReportId,
            ct);
        var sourcePayloadRevision = P10ProductionRequiredNullableInt(
            job.SourcePayloadRevision,
            "sourcePayloadRevision");
        var sourcePayloadHash = P10ProductionRequiredPin(
            job.SourcePayloadHash,
            "sourcePayloadHash");
        var sourceLifecycleRevision = P10ProductionRequiredNullableInt(
            job.SourceLifecycleRevision,
            "sourceLifecycleRevision");
        HarnessAssert.Equal(
            sourcePayloadRevision,
            finalReport.GetValue("payloadRevision").ToInt32(),
            "P10 production DIRECT job/report payload revision");
        HarnessAssert.Equal(
            sourcePayloadHash,
            P10ProductionRequiredBsonString(finalReport, "payloadHash"),
            "P10 production DIRECT job/report payload hash");
        HarnessAssert.Equal(
            sourceLifecycleRevision,
            finalReport.GetValue("lifecycleRevision").ToInt32(),
            "P10 production DIRECT job/report lifecycle revision");

        var configBundleHash = StatisticReconciliationCanonicalJson.HashObject(
            new
            {
                schema = "P10_P8_CONFIG_BUNDLE_PIN_V1",
                ownerId = job.DynamicFormTemplateId,
                configId = P10ProductionRequiredPin(job.ConfigId, "configId"),
                configVersionId = P10ProductionRequiredPin(
                    job.ConfigVersionId,
                    "configVersionId"),
                configVersionNo = P10ProductionRequiredNullableInt(
                    job.ConfigVersionNo,
                    "configVersionNo"),
                configRevision = P10ProductionRequiredNullableLong(
                    job.ConfigRevision,
                    "configRevision"),
                configHash = P10ProductionRequiredPin(
                    job.ConfigHash,
                    "configHash")
            });
        HarnessAssert.Equal(
            schema.ConfigBundleHash,
            configBundleHash,
            "P10 production DIRECT config bundle hash");

        _fixture = fixture with
        {
            P9ResultId = job.Id,
            P9RunId = job.Id,
            P9GenerationId = generationId,
            P9GenerationHash = generationHash,
            SourcePayloadHash = sourcePayloadHash,
            SourcePayloadRevision = sourcePayloadRevision,
            SourceLifecycleRevision = sourceLifecycleRevision,
            ConfigId = P10ProductionRequiredPin(job.ConfigId, "configId"),
            ConfigVersionId = P10ProductionRequiredPin(
                job.ConfigVersionId,
                "configVersionId"),
            ConfigRevision = P10ProductionRequiredNullableLong(
                job.ConfigRevision,
                "configRevision"),
            ConfigHash = P10ProductionRequiredPin(job.ConfigHash, "configHash"),
            ConfigBundleHash = configBundleHash,
            FlowPayloadHash = P10ProductionRequiredPin(
                job.FlowPayloadHash,
                "flowPayloadHash"),
            ConceptKey = P10ProductionDirectFieldKey,
            Grain = "MONTH"
        };

        return new P10ProductionDirectFixturePins(
            P9ResultId: job.Id,
            P9RunId: job.Id,
            P9GenerationId: generationId,
            P9GenerationHash: generationHash,
            P9StateRevision: job.StateRevision,
            P9StateHash: P10ProductionRequiredPin(job.StateHash, "stateHash"),
            P9FreshnessState: P10ProductionRequiredPin(
                job.FreshnessState,
                "freshnessState"),
            RunKind: P10ProductionRequiredPin(job.RunKind, "runKind"),
            RouteId: P10ProductionRequiredPin(job.RouteId, "routeId"),
            CapabilityId: P10ProductionRequiredPin(
                job.CapabilityId,
                "capabilityId"),
            ReceiptId: P10ProductionRequiredPin(job.ReceiptId, "receiptId"),
            CommandId: P10ProductionRequiredPin(job.CommandId, "commandId"),
            RequestHash: P10ProductionRequiredPin(job.RequestHash, "requestHash"),
            ImmutableHeaderHash: P10ProductionRequiredPin(
                job.ImmutableHeaderHash,
                "immutableHeaderHash"),
            ActorUserId: P10ProductionRequiredPin(
                job.ActorUserId,
                "actorUserId"),
            TenantUnitId: P10ProductionRequiredPin(
                job.TenantUnitId,
                "tenantUnitId"),
            ScopeType: P10ProductionRequiredPin(job.ScopeType, "scopeType"),
            ScopeId: P10ProductionRequiredPin(job.ScopeId, "scopeId"),
            ScopeKind: job.ScopeKind,
            WorkId: P10ProductionRequiredPin(job.WorkId, "workId"),
            WorkAssignmentId: P10ProductionRequiredPin(
                job.WorkAssignmentId,
                "workAssignmentId"),
            SourceReportId: P10ProductionRequiredPin(
                job.SourceReportId,
                "sourceReportId"),
            SourcePayloadRevision: sourcePayloadRevision,
            SourcePayloadHash: sourcePayloadHash,
            SourceLifecycleRevision: sourceLifecycleRevision,
            SourceLifecycleEventKey: P10ProductionRequiredPin(
                job.SourceLifecycleEventKey,
                "sourceLifecycleEventKey"),
            SourceMembershipSignature: P10ProductionRequiredPin(
                job.SourceMembershipSignature,
                "sourceMembershipSignature"),
            SourceStatus: P10ProductionRequiredPin(
                job.SourceStatus,
                "sourceStatus"),
            DirectSourceRevision: P10ProductionRequiredNullableLong(
                job.DirectSourceRevision,
                "directSourceRevision"),
            DirectPublicationRevision: P10ProductionRequiredNullableLong(
                job.DirectPublicationRevision,
                "directPublicationRevision"),
            PublicationScopeKey: P10ProductionRequiredPin(
                job.PublicationScopeKey,
                "publicationScopeKey"),
            ConfigId: P10ProductionRequiredPin(job.ConfigId, "configId"),
            ConfigVersionId: P10ProductionRequiredPin(
                job.ConfigVersionId,
                "configVersionId"),
            ConfigVersionNo: P10ProductionRequiredNullableInt(
                job.ConfigVersionNo,
                "configVersionNo"),
            ConfigRevision: P10ProductionRequiredNullableLong(
                job.ConfigRevision,
                "configRevision"),
            ConfigHash: P10ProductionRequiredPin(job.ConfigHash, "configHash"),
            ConfigBundleHash: configBundleHash,
            CatalogVersion: P10ProductionRequiredPin(
                job.CatalogVersion,
                "catalogVersion"),
            CatalogRawSha256: P10ProductionRequiredPin(
                job.CatalogRawSha256,
                "catalogRawSha256"),
            CatalogSemanticSha256: P10ProductionRequiredPin(
                job.CatalogSemanticSha256,
                "catalogSemanticSha256"),
            SchemaRawSha256: P10ProductionRequiredPin(
                job.SchemaRawSha256,
                "schemaRawSha256"),
            SchemaSemanticSha256: P10ProductionRequiredPin(
                job.SchemaSemanticSha256,
                "schemaSemanticSha256"),
            StageLockSha256: P10ProductionRequiredPin(
                job.StageLockSha256,
                "stageLockSha256"),
            CandidateChainId: P10ProductionRequiredPin(
                job.CandidateChainId,
                "candidateChainId"),
            CandidatePromptId: P10ProductionRequiredPin(
                job.CandidatePromptId,
                "candidatePromptId"),
            DynamicFormVersionId: job.DynamicFormTemplateId,
            DynamicFormFamilyId: P10ProductionRequiredPin(
                job.DynamicFormFamilyId,
                "dynamicFormFamilyId"),
            DynamicFormVersionNo: P10ProductionRequiredNullableInt(
                job.DynamicFormVersionNo,
                "dynamicFormVersionNo"),
            DynamicFormSchemaHash: P10ProductionRequiredPin(
                job.DynamicFormSchemaHash,
                "dynamicFormSchemaHash"),
            DynamicFormTemplateCode: P10ProductionRequiredPin(
                job.DynamicFormTemplateCode,
                "dynamicFormTemplateCode"),
            DynamicFormTemplateName: P10ProductionRequiredPin(
                job.DynamicFormTemplateName,
                "dynamicFormTemplateName"),
            FlowTemplateId: P10ProductionRequiredPin(
                job.FlowTemplateId,
                "flowTemplateId"),
            FlowTemplateVersionId: P10ProductionRequiredPin(
                job.FlowTemplateVersionId,
                "flowTemplateVersionId"),
            FlowTemplateVersionNo: P10ProductionRequiredNullableInt(
                job.FlowTemplateVersionNo,
                "flowTemplateVersionNo"),
            FlowFamilyRevision: P10ProductionRequiredNullableInt(
                job.FlowFamilyRevision,
                "flowFamilyRevision"),
            FlowContributionOriginVersionId: job.FlowContributionOriginVersionId,
            FlowInstanceId: P10ProductionRequiredPin(
                job.FlowInstanceId,
                "flowInstanceId"),
            FlowInstanceRevision: P10ProductionRequiredNullableLong(
                job.FlowInstanceRevision,
                "flowInstanceRevision"),
            FlowInstanceState: P10ProductionRequiredPin(
                job.FlowInstanceState,
                "flowInstanceState"),
            FlowEffectiveStatus: P10ProductionRequiredPin(
                job.FlowEffectiveStatus,
                "flowEffectiveStatus"),
            FlowExecutionEpoch: P10ProductionRequiredNullableInt(
                job.FlowExecutionEpoch,
                "flowExecutionEpoch"),
            FlowExecutionEpochId: P10ProductionRequiredPin(
                job.FlowExecutionEpochId,
                "flowExecutionEpochId"),
            FlowExecutionEpochRevision: P10ProductionRequiredNullableLong(
                job.FlowExecutionEpochRevision,
                "flowExecutionEpochRevision"),
            FlowExecutionEpochState: P10ProductionRequiredPin(
                job.FlowExecutionEpochState,
                "flowExecutionEpochState"),
            FlowBranchId: P10ProductionRequiredPin(
                job.FlowBranchId,
                "flowBranchId"),
            FlowStepId: P10ProductionRequiredPin(job.FlowStepId, "flowStepId"),
            FlowAttemptNo: P10ProductionRequiredNullableInt(
                job.FlowAttemptNo,
                "flowAttemptNo"),
            FlowStepInstanceId: P10ProductionRequiredPin(
                job.FlowStepInstanceId,
                "flowStepInstanceId"),
            FlowStepInstanceRevision: P10ProductionRequiredNullableLong(
                job.FlowStepInstanceRevision,
                "flowStepInstanceRevision"),
            FlowStepInstanceState: P10ProductionRequiredPin(
                job.FlowStepInstanceState,
                "flowStepInstanceState"),
            FlowPayloadHash: P10ProductionRequiredPin(
                job.FlowPayloadHash,
                "flowPayloadHash"),
            FlowCatalogVersion: P10ProductionRequiredPin(
                job.FlowCatalogVersion,
                "flowCatalogVersion"),
            FlowCatalogSemanticHash: P10ProductionRequiredPin(
                job.FlowCatalogSemanticHash,
                "flowCatalogSemanticHash"),
            FlowContributionPolicy: P10ProductionRequiredPin(
                job.FlowContributionPolicy,
                "flowContributionPolicy"),
            FlowContributionPolicyHash: P10ProductionRequiredPin(
                job.FlowContributionPolicyHash,
                "flowContributionPolicyHash"),
            FlowContributionWarning: P10ProductionRequiredPin(
                job.FlowContributionWarning,
                "flowContributionWarning"),
            FlowContributionOperationVersion: P10ProductionRequiredPin(
                job.FlowContributionOperationVersion,
                "flowContributionOperationVersion"),
            FlowContributionLedgerHash: P10ProductionRequiredPin(
                job.FlowContributionLedgerHash,
                "flowContributionLedgerHash"),
            FlowContributionReversalBaselineHash: P10ProductionRequiredPin(
                job.FlowContributionReversalBaselineHash,
                "flowContributionReversalBaselineHash"),
            FlowContributionSourceCount: job.FlowContributionSourceCount,
            NonFlowContributionSourceCount: job.NonFlowContributionSourceCount,
            FlowContributionTargetCount: job.FlowContributionTargetCount,
            PeriodKey: P10ProductionRequiredPin(job.PeriodKey, "periodKey"),
            PeriodInstanceKey: P10ProductionRequiredPin(
                job.PeriodInstanceKey,
                "periodInstanceKey"),
            PeriodKind: P10ProductionRequiredPin(job.PeriodKind, "periodKind"),
            PeriodStartUtc: job.PeriodStartUtc,
            PeriodEndUtc: job.PeriodEndUtc,
            MappingReceiptId: mapping.ReceiptId,
            MappingProvenanceId: mapping.ProvenanceId,
            MappingProvenanceHash: mapping.ProvenanceHash,
            MappingEventId: mapping.EventId,
            MappingOutboxId: mapping.OutboxId,
            MappingResultSemanticHash: mapping.ResultSemanticHash,
            MappingResultSnapshotHash: mapping.ResultSnapshotHash,
            MappingContributionPolicyHash:
                mapping.ContributionPolicyHash,
            DirectStoreDigests: job.DirectStoreDigests
                .OrderBy(digest => digest.Store, StringComparer.Ordinal)
                .Select(digest => new P10ProductionDirectStoreDigestPin(
                    digest.Store,
                    digest.RowCount,
                    digest.Sha256))
                .ToArray(),
            OwnedCleanupHandles: ownedCleanup.ToArray());
    }

    private async Task RetireP10SyntheticSeedPublicationAsync(
        P10Fixture fixture,
        CancellationToken ct)
    {
        var jobs = RequireDatabase()
            .GetCollection<WorkReportStatisticRebuildJob>(
                P10ProductionLifecycleJobCollection);
        var seed = await jobs
            .Find(job => job.Id == fixture.P9RunId && !job.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.True(
            string.Equals(
                seed.RunKind,
                WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection,
                StringComparison.Ordinal) &&
            string.Equals(
                seed.Status,
                WorkReportStatisticRebuildJobStatuses.Completed,
                StringComparison.Ordinal) &&
            string.Equals(seed.WorkId, fixture.WorkId, StringComparison.Ordinal) &&
            string.Equals(
                seed.PeriodInstanceKey,
                fixture.PeriodInstanceKey,
                StringComparison.Ordinal) &&
            string.Equals(
                seed.DynamicFormTemplateId,
                fixture.DynamicFormVersionId,
                StringComparison.Ordinal) &&
            seed.IsCurrentPublication &&
            string.Equals(
                seed.FreshnessState,
                WorkReportStatisticRebuildJobFreshnessStates.Fresh,
                StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(seed.PublicationScopeKey) &&
            !string.IsNullOrWhiteSpace(seed.StateHash) &&
            !string.IsNullOrWhiteSpace(seed.GenerationId) &&
            !string.IsNullOrWhiteSpace(seed.GenerationHash),
            "P10 synthetic seed publication retirement precondition drifted.");

        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var now = DateTime.UtcNow;
        var retired = await jobs.UpdateOneAsync(
            fb.Eq(job => job.Id, seed.Id) &
            fb.Eq(job => job.RunKind, seed.RunKind) &
            fb.Eq(job => job.Status, seed.Status) &
            fb.Eq(job => job.WorkId, seed.WorkId) &
            fb.Eq(job => job.PeriodInstanceKey, seed.PeriodInstanceKey) &
            fb.Eq(job => job.DynamicFormTemplateId, seed.DynamicFormTemplateId) &
            fb.Eq(job => job.PublicationScopeKey, seed.PublicationScopeKey) &
            fb.Eq(job => job.GenerationId, seed.GenerationId) &
            fb.Eq(job => job.GenerationHash, seed.GenerationHash) &
            fb.Eq(job => job.DirectPublicationRevision, seed.DirectPublicationRevision) &
            fb.Eq(job => job.StateRevision, seed.StateRevision) &
            fb.Eq(job => job.StateHash, seed.StateHash) &
            fb.Eq(job => job.IsCurrentPublication, true) &
            fb.Eq(
                job => job.FreshnessState,
                WorkReportStatisticRebuildJobFreshnessStates.Fresh) &
            fb.Eq(job => job.IsDeleted, false),
            Builders<WorkReportStatisticRebuildJob>.Update
                .Set(job => job.IsCurrentPublication, false)
                .Set(
                    job => job.FreshnessState,
                    WorkReportStatisticRebuildJobFreshnessStates.Stale)
                .Set(job => job.StaleReason, "LIFECYCLE_SUPERSEDED")
                .Set(job => job.UpdatedAtUtc, now)
                .Set(job => job.UpdatedByUserId, Actor("admin").Id),
            cancellationToken: ct);
        HarnessAssert.Equal(
            1L,
            retired.MatchedCount,
            "P10 synthetic seed publication retirement match");
        HarnessAssert.Equal(
            1L,
            retired.ModifiedCount,
            "P10 synthetic seed publication retirement write");

        var observed = await jobs
            .Find(job => job.Id == seed.Id && !job.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.True(
            !observed.IsCurrentPublication &&
            string.Equals(
                observed.FreshnessState,
                WorkReportStatisticRebuildJobFreshnessStates.Stale,
                StringComparison.Ordinal) &&
            string.Equals(
                observed.StaleReason,
                "LIFECYCLE_SUPERSEDED",
                StringComparison.Ordinal) &&
            string.Equals(observed.Status, seed.Status, StringComparison.Ordinal) &&
            observed.StateRevision == seed.StateRevision &&
            string.Equals(observed.StateHash, seed.StateHash, StringComparison.Ordinal) &&
            string.Equals(
                observed.GenerationId,
                seed.GenerationId,
                StringComparison.Ordinal) &&
            string.Equals(
                observed.GenerationHash,
                seed.GenerationHash,
                StringComparison.Ordinal) &&
            observed.DirectPublicationRevision == seed.DirectPublicationRevision,
            "P10 synthetic seed publication retirement postcondition drifted.");
    }

    private async Task RequireP10ProductionUniqueCurrentPublicationAsync(
        WorkReportStatisticRebuildJob job,
        P10Fixture fixture,
        CancellationToken ct)
    {
        var work = await RequireDatabase()
            .GetCollection<Work>("works")
            .Find(item => item.Id == fixture.WorkId && !item.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.Equal(
            work.DirectSourceRevision,
            P10ProductionRequiredNullableLong(
                job.DirectSourceRevision,
                "directSourceRevision"),
            "P10 production DIRECT authoritative source revision");
        HarnessAssert.Equal(
            WorkReportStatisticRebuildJobFreshnessStates.Fresh,
            job.FreshnessState,
            "P10 production DIRECT publication freshness");

        var current = await RequireDatabase()
            .GetCollection<WorkReportStatisticRebuildJob>(
                P10ProductionLifecycleJobCollection)
            .Find(item =>
                item.WorkId == fixture.WorkId &&
                item.PeriodInstanceKey == fixture.PeriodInstanceKey &&
                item.DynamicFormTemplateId == fixture.DynamicFormVersionId &&
                item.RunKind ==
                    WorkReportStatisticRebuildJobRunKinds
                        .LifecycleDirectProjection &&
                item.Status == WorkReportStatisticRebuildJobStatuses.Completed &&
                item.IsCurrentPublication &&
                !item.IsDeleted)
            .ToListAsync(ct);
        HarnessAssert.Equal(
            1,
            current.Count,
            "P10 production DIRECT current publication cardinality");
        HarnessAssert.Equal(
            job.Id,
            current[0].Id,
            "P10 production DIRECT current publication identity");
    }
    private async Task RebuildP10ProductionAssignmentReadModelAsync(
        P10Fixture fixture,
        List<P10CleanupHandle> ownedCleanup,
        CancellationToken ct)
    {
        var mongo = RequireMongo();
        var context = new MongoDbContext(
            Microsoft.Extensions.Options.Options.Create(new MongoOptions
            {
                ConnectionString = mongo.ConnectionString,
                Database = mongo.DatabaseName
            }));
        var admin = Actor("admin");
        var executor = Actor("executor");
        var executor2 = Actor("executor2");
        var insufficient = Actor("insufficient");

        await new DocRoleReadModelProjectionService(context)
            .RebuildAssignmentAsync(
                fixture.ScopeAssignmentId,
                admin.Id,
                ct);

        var assignment = await context.WorkAssignments
            .Find(item =>
                item.Id == fixture.ScopeAssignmentId &&
                !item.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.True(
            string.IsNullOrWhiteSpace(assignment.ParentAssignmentId) &&
            assignment.Assignees.Count == 1 &&
            assignment.Assignees[0].UserId == executor.Id &&
            ((assignment.LeaderWatchers.Count == 0 &&
              assignment.LeaderWatcherUserIds.Count == 2 &&
              assignment.LeaderWatcherUserIds.Contains(
                  executor2.Id,
                  StringComparer.Ordinal) &&
              assignment.LeaderWatcherUserIds.Contains(
                  insufficient.Id,
                  StringComparer.Ordinal)) ||
             (assignment.LeaderWatchers.Count == 1 &&
              assignment.LeaderWatchers[0].UserId == executor.Id &&
              assignment.LeaderWatchers[0].UnitId == executor.UnitId &&
              assignment.LeaderWatcherUserIds.Count == 3 &&
              assignment.LeaderWatcherUserIds.Contains(
                  executor.Id,
                  StringComparer.Ordinal) &&
              assignment.LeaderWatcherUserIds.Contains(
                  executor2.Id,
                  StringComparer.Ordinal) &&
              assignment.LeaderWatcherUserIds.Contains(
                  insufficient.Id,
                  StringComparer.Ordinal))),
            "P10 production DIRECT assignment projection source drifted.");

        var expectedRows = new Dictionary<
            string,
            (IReadOnlyList<DocRoleType> Roles, UserRef? User)>(
            StringComparer.Ordinal);
        expectedRows[assignment.CreatedByUserId!] =
            ([DocRoleType.ASSIGNER], null);
        if (expectedRows.TryGetValue(executor.Id, out var executorSeed))
        {
            expectedRows[executor.Id] =
                ([.. executorSeed.Roles, DocRoleType.ASSIGNEE],
                    assignment.Assignees[0]);
        }
        else
        {
            expectedRows[executor.Id] =
                ([DocRoleType.ASSIGNEE], assignment.Assignees[0]);
        }
        if (assignment.LeaderWatchers.Count == 1)
        {
            var expected = expectedRows[executor.Id];
            expectedRows[executor.Id] =
                ([.. expected.Roles, DocRoleType.ASSIGNMENT_LEADER_WATCH],
                    expected.User ?? assignment.LeaderWatchers[0]);
        }

        var rows = await context.AssignmentListDocRoles
            .Find(item =>
                item.AssignmentId == fixture.ScopeAssignmentId &&
                !item.IsDeleted)
            .ToListAsync(ct);
        HarnessAssert.Equal(
            expectedRows.Count,
            rows.Count,
            "P10 production DIRECT assignment projection row count");
        foreach (var (userId, expected) in expectedRows)
        {
            var row = rows.Single(item => item.UserId == userId);
            HarnessAssert.True(
                DynamicFlowRuntimeReadModelContract.MatchesAssignment(
                    row,
                    assignment,
                    userId,
                    expected.Roles,
                    expected.User),
                $"P10 production DIRECT assignment projection drifted for {userId}.");
            RegisterP10ProductionCleanup(
                ownedCleanup,
                "assignment_list_doc_roles",
                row.Id);
        }

        var executorRow = rows.Single(item => item.UserId == executor.Id);
        HarnessAssert.True(
            executorRow.Roles.Contains(DocRoleType.ASSIGNEE) &&
            (assignment.LeaderWatchers.Count == 0 ||
             executorRow.Roles.Contains(
                 DocRoleType.ASSIGNMENT_LEADER_WATCH)) &&
            executorRow.VisibleUnitIds.Contains(
                executor.UnitId,
                StringComparer.Ordinal),
            "P10 production DIRECT executor assignment visibility drifted.");
    }

    private async Task<P10ProductionSchemaConfigPins>
        PrepareProductionDirectSchemaAndConfigAsync(
            P10Fixture fixture,
            List<P10CleanupHandle> ownedCleanup,
            bool useNativeNullPeriodPair,
            bool includeSecondStatisticField,
            CancellationToken ct)
    {
        const string blocksJson = "[]";
        const string emptyValuesJson = "[]";
        const string emptyFieldValuesJson = "{\"values\":{}}";
        const string emptyTableValuesJson = "{\"blocks\":[]}";
        const string emptySummaryJson = "{}";
        var database = RequireDatabase();
        var now = DateTime.UtcNow;
        var admin = Actor("admin");
        var executor = Actor("executor");
        var fieldsJson = includeSecondStatisticField
            ? P10ProductionDirectTwoFieldsJson
            : P10ProductionDirectFieldsJson;
        var publishedSchema = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            1,
            P10ProductionDirectSectionsJson,
            fieldsJson,
            blocksJson);

        var templates = database.GetCollection<BsonDocument>(
            "dynamic_form_templates");
        var templateUpdate = await templates.UpdateOneAsync(
            new BsonDocument(
                "_id",
                ObjectId.Parse(fixture.DynamicFormVersionId)),
            Builders<BsonDocument>.Update
                .Set("sectionsJson", P10ProductionDirectSectionsJson)
                .Set("fieldsJson", fieldsJson)
                .Set("blocksJson", blocksJson)
                .Set("publishedSchemaSnapshotJson", publishedSchema.Json)
                .Set("publishedSchemaHash", publishedSchema.Sha256)
                .Set("updatedAtUtc", now)
                .Set("updatedByUserId", ObjectId.Parse(admin.Id))
                .Unset("statisticConfigId")
                .Unset("statisticConfigPreviousVersionId")
                .Unset("statisticConfigVersionId")
                .Unset("statisticConfigVersionNo")
                .Unset("statisticConfigRevision")
                .Unset("statisticConfigStatus")
                .Unset("statisticConfigHash")
                .Unset("statisticConfigDependencyPins")
                .Unset("statisticConfigSections")
                .Unset("statisticConfigSnapshots")
                .Unset("statisticConfigUpdatedAtUtc")
                .Unset("statisticConfigUpdatedByUserId")
                .Unset("statisticConfigUpdateMonthKey")
                .Unset("p9CanonicalConfigPayloadJson"),
            cancellationToken: ct);
        HarnessAssert.Equal(
            1L,
            templateUpdate.MatchedCount,
            "P10 production DIRECT form template");

        var flowVersions = database.GetCollection<DynamicFlowTemplateVersion>(
            "dynamic_flow_template_versions");
        var flowVersion = await flowVersions
            .Find(version =>
                version.Id == fixture.FlowTemplateVersionId &&
                version.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                !version.IsDeleted)
            .SingleAsync(ct);
        var rootFlowPayloadHash = flowVersion.PayloadHash;
        HarnessAssert.True(
            flowVersion.VersionNo == 1 &&
            string.Equals(
                flowVersion.TemplateId,
                fixture.FlowTemplateId,
                StringComparison.Ordinal) &&
            flowVersion.OriginFamilyId is null &&
            flowVersion.OriginVersionId is null,
            "P10 production DIRECT root flow version lineage");
        var flowPayload = JsonNode.Parse(flowVersion.PayloadJson) as JsonObject
            ?? throw new InvalidOperationException(
                "P10 production DIRECT flow payload is not an object.");
        var formNodes = flowPayload["formNodes"] as JsonArray
            ?? throw new InvalidOperationException(
                "P10 production DIRECT flow payload lacks formNodes.");
        var formNode = formNodes
            .OfType<JsonObject>()
            .Single(node => string.Equals(
                node["dynamicFormTemplateId"]?.GetValue<string>(),
                fixture.DynamicFormVersionId,
                StringComparison.Ordinal));
        formNode["dynamicFormSchemaHash"] = publishedSchema.Sha256;
        formNode["dynamicFormSnapshotHash"] = publishedSchema.Sha256;
        var flowNode = (flowPayload["nodes"] as JsonArray)?
            .OfType<JsonObject>()
            .Single(node => string.Equals(
                node["nodeId"]?.GetValue<string>(),
                "P10_CORE_STEP",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "P10 production DIRECT flow payload lacks its core step.");
        flowNode["declaredRoles"] = new JsonArray("ASSIGNEE");
        var actorPolicy = (flowPayload["actorPolicies"] as JsonArray)?
            .OfType<JsonObject>()
            .Single()
            ?? throw new InvalidOperationException(
                "P10 production DIRECT flow payload lacks its actor policy.");
        actorPolicy["policyId"] = "p10-actor-assignee-root";
        actorPolicy["actorRole"] = "ASSIGNEE";
        var fieldPolicies = flowPayload["fieldPolicies"] as JsonArray
            ?? throw new InvalidOperationException(
                "P10 production DIRECT flow payload lacks field policies.");
        var fieldPolicy = fieldPolicies
            .OfType<JsonObject>()
            .Single();
        fieldPolicy["policyId"] = "p10-fields-assignee-amount";
        fieldPolicy["dynamicFormTemplateId"] = fixture.DynamicFormVersionId;
        fieldPolicy["actorRole"] = "ASSIGNEE";
        fieldPolicy["fieldId"] = P10ProductionDirectFieldId;
        fieldPolicy["fieldKey"] = P10ProductionDirectFieldKey;
        if (includeSecondStatisticField)
        {
            var secondFieldPolicy = (JsonObject)fieldPolicy.DeepClone();
            secondFieldPolicy["policyId"] =
                "p10-fields-assignee-amount-secondary";
            secondFieldPolicy["fieldId"] =
                P10ProductionDirectSecondFieldId;
            secondFieldPolicy["fieldKey"] =
                P10ProductionDirectSecondFieldKey;
            fieldPolicies.Add(secondFieldPolicy);
        }
        var canonicalFlow =
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                flowPayload.ToJsonString(),
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true));
        flowVersion.PayloadJson = canonicalFlow.CanonicalJson;
        flowVersion.PayloadHash = canonicalFlow.PayloadHash;
        flowVersion.SchemaVersion = canonicalFlow.Payload.SchemaVersion;
        flowVersion.AdapterVersion = canonicalFlow.AdapterVersion;
        flowVersion.CatalogVersion = P10ProductionRequiredPin(
            canonicalFlow.Payload.CatalogVersion,
            "canonicalFlow.catalogVersion");
        flowVersion.CatalogSemanticHash = P10ProductionRequiredPin(
            canonicalFlow.Payload.CatalogSemanticHash,
            "canonicalFlow.catalogSemanticHash");
        flowVersion.BlockedUntilPhase = canonicalFlow.BlockedUntilPhase;
        flowVersion.UpdatedAtUtc = now;
        flowVersion.UpdatedByUserId = admin.Id;
        DynamicFlowContributionPolicyContract.ApplyLockedPolicy(
            flowVersion,
            DynamicFlowContributionPolicyContract.ResolveLockSelection(
                DynamicFlowContributionPolicyContract.Exclude,
                acknowledgeWarning: null));
        DynamicFlowContributionPolicyContract.ValidateLockedPolicy(flowVersion);
        var flowVersionWrite = await flowVersions.ReplaceOneAsync(
            version =>
                version.Id == fixture.FlowTemplateVersionId &&
                version.TemplateId == fixture.FlowTemplateId &&
                version.VersionNo == 1 &&
                version.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                version.OriginFamilyId == null &&
                version.OriginVersionId == null &&
                version.PayloadHash == rootFlowPayloadHash &&
                !version.IsDeleted,
            flowVersion,
            cancellationToken: ct);
        HarnessAssert.Equal(
            1L,
            flowVersionWrite.MatchedCount,
            "P10 production DIRECT root EXCLUDE flow version");

        var includeDocument = flowVersion.ToBsonDocument();
        var includeVersionId = ObjectId.GenerateNewId();
        includeDocument["_id"] = includeVersionId;
        includeDocument["versionNo"] = 2;
        includeDocument["originFamilyId"] = ObjectId.Parse(
            fixture.FlowTemplateId);
        includeDocument["originVersionId"] = ObjectId.Parse(flowVersion.Id);
        includeDocument["createdAtUtc"] = now;
        includeDocument["updatedAtUtc"] = now;
        includeDocument["createdByUserId"] = ObjectId.Parse(admin.Id);
        includeDocument["updatedByUserId"] = ObjectId.Parse(admin.Id);
        includeDocument.Remove("contributionPolicy");
        includeDocument.Remove("contributionPolicyHash");
        includeDocument.Remove("contributionWarning");
        var includeVersion =
            BsonSerializer.Deserialize<DynamicFlowTemplateVersion>(
                includeDocument);
        includeVersion.LockedAtUtc = now;
        includeVersion.LockedByUserId = admin.Id;
        DynamicFlowContributionPolicyContract.EnsureIncludeOrigin(
            includeVersion,
            flowVersion);
        DynamicFlowContributionPolicyContract.ApplyLockedPolicy(
            includeVersion,
            DynamicFlowContributionPolicyContract.ResolveLockSelection(
                DynamicFlowContributionPolicyContract.Include,
                acknowledgeWarning: true));
        DynamicFlowContributionPolicyContract.ValidateLockedPolicy(
            includeVersion);
        DynamicFlowContributionPolicyContract.EnsureIncludeOrigin(
            includeVersion,
            flowVersion);
        HarnessAssert.True(
            !string.Equals(
                includeVersion.Id,
                flowVersion.Id,
                StringComparison.Ordinal) &&
            includeVersion.VersionNo == 2 &&
            string.Equals(
                includeVersion.PayloadHash,
                flowVersion.PayloadHash,
                StringComparison.Ordinal),
            "P10 production DIRECT INCLUDE flow version lineage");
        await flowVersions.InsertOneAsync(
            includeVersion,
            cancellationToken: ct);
        RegisterP10ProductionCleanup(
            ownedCleanup,
            "dynamic_flow_template_versions",
            includeVersion.Id);

        await P10ProductionRequireMatchedAsync(
            database.GetCollection<BsonDocument>("dynamic_flow_templates")
                .UpdateOneAsync(
                    new BsonDocument
                    {
                        ["_id"] = ObjectId.Parse(fixture.FlowTemplateId),
                        ["familyRevision"] = 1L,
                        ["status"] = DynamicFlowTemplateStatuses.Active,
                        ["currentVersionId"] = ObjectId.Parse(flowVersion.Id),
                        ["currentVersionNo"] = 1,
                        ["hasLockedVersion"] = true,
                        ["isDeleted"] = false
                    },
                    Builders<BsonDocument>.Update
                        .Set("familyRevision", 3L)
                        .Set(
                            "currentVersionId",
                            ObjectId.Parse(includeVersion.Id))
                        .Set("currentVersionNo", includeVersion.VersionNo)
                        .Set("currentVersionHash", canonicalFlow.PayloadHash)
                        .Set("updatedAtUtc", now)
                        .Set("updatedByUserId", ObjectId.Parse(admin.Id)),
                    cancellationToken: ct),
            "P10 production DIRECT flow family");
        await P10ProductionRequireMatchedAsync(
            database.GetCollection<BsonDocument>("dynamic_flow_instances")
                .UpdateOneAsync(
                    new BsonDocument
                    {
                        ["_id"] = ObjectId.Parse(fixture.FlowInstanceId),
                        ["workId"] = ObjectId.Parse(fixture.WorkId),
                        ["flowTemplateId"] = ObjectId.Parse(fixture.FlowTemplateId),
                        ["flowTemplateVersionId"] = ObjectId.Parse(flowVersion.Id),
                        ["flowTemplateVersionNo"] = 1,
                        ["flowPayloadHash"] = rootFlowPayloadHash,
                        ["isDeleted"] = false
                    },
                    Builders<BsonDocument>.Update
                        .Set(
                            "flowTemplateVersionId",
                            ObjectId.Parse(includeVersion.Id))
                        .Set(
                            "flowTemplateVersionNo",
                            includeVersion.VersionNo)
                        .Set("flowPayloadHash", canonicalFlow.PayloadHash)
                        .Set("catalogVersion", includeVersion.CatalogVersion)
                        .Set(
                            "catalogSemanticHash",
                            includeVersion.CatalogSemanticHash)
                        .Set("nextEventSequence", 1L)
                        .Set("updatedAtUtc", now)
                        .Set("updatedByUserId", ObjectId.Parse(admin.Id)),
                    cancellationToken: ct),
            "P10 production DIRECT flow instance");

        HarnessAssert.Equal(
            fixture.UnitAId,
            executor.UnitId,
            "P10 production DIRECT executor unit");
        var assignmentUpdate = Builders<BsonDocument>.Update
            .Set("createdByUserId", ObjectId.Parse(executor.Id))
            .Set("issuedByUnitId", ObjectId.Parse(admin.UnitId))
            .Set(
                "assignees",
                new BsonArray
                {
                    new BsonDocument
                    {
                        { "userId", ObjectId.Parse(executor.Id) },
                        { "unitId", ObjectId.Parse(fixture.UnitAId) }
                    }
                })
            .Set(
                "targetUnitIds",
                new BsonArray { ObjectId.Parse(fixture.UnitAId) })
            .Set("flowTemplateVersionNo", includeVersion.VersionNo)
            .Set("dynamicFormSchemaHash", publishedSchema.Sha256)
            .Set("updatedAtUtc", now)
            .Set("updatedByUserId", ObjectId.Parse(admin.Id));
        await P10ProductionRequireMatchedAsync(
            database.GetCollection<BsonDocument>("work_assignments")
                .UpdateOneAsync(
                    new BsonDocument
                    {
                        ["_id"] = ObjectId.Parse(fixture.ScopeAssignmentId),
                        ["workId"] = ObjectId.Parse(fixture.WorkId),
                        ["flowTemplateId"] = ObjectId.Parse(fixture.FlowTemplateId),
                        ["flowTemplateVersionNo"] = 1,
                        ["createdByUserId"] = ObjectId.Parse(executor.Id),
                        ["isActive"] = true,
                        ["isDeleted"] = false
                    },
                    assignmentUpdate,
                    cancellationToken: ct),
            "P10 production DIRECT scope assignment");
        await P10ProductionRequireMatchedAsync(
            database.GetCollection<BsonDocument>("work_assignments")
                .UpdateOneAsync(
                    new BsonDocument
                    {
                        ["_id"] = ObjectId.Parse(fixture.SiblingAssignmentId),
                        ["workId"] = ObjectId.Parse(fixture.WorkId),
                        ["flowTemplateId"] = ObjectId.Parse(fixture.FlowTemplateId),
                        ["flowTemplateVersionNo"] = 1,
                        ["createdByUserId"] = ObjectId.Parse(admin.Id),
                        ["isActive"] = true,
                        ["isDeleted"] = false
                    },
                    assignmentUpdate,
                    cancellationToken: ct),
            "P10 production DIRECT sibling assignment");
        var productionSibling = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .Find(value => value.Id == fixture.SiblingAssignmentId)
            .SingleAsync(ct);
        HarnessAssert.True(
            productionSibling.CreatedByUserId == executor.Id &&
            productionSibling.IssuedByUnitId == admin.UnitId &&
            productionSibling.TargetUnitIds is { Count: 1 } targetUnitIds &&
            targetUnitIds[0] == fixture.UnitAId &&
            productionSibling.Assignees is { Count: 1 } assignees &&
            assignees[0].UserId == executor.Id &&
            assignees[0].UnitId == fixture.UnitAId,
            "P10 production DIRECT EMPTY sibling authorization");

        var initialPayloadHash = ComputeP10ProductionPayloadHash(
            emptyValuesJson,
            emptyFieldValuesJson,
            emptyTableValuesJson,
            emptySummaryJson);
        var initialPayloadSize = P10ProductionPayloadSize(
            emptyValuesJson,
            emptyFieldValuesJson,
            emptyTableValuesJson,
            emptySummaryJson);
        await P10ProductionRequireMatchedAsync(
            database.GetCollection<BsonDocument>("work_report_payloads")
                .UpdateOneAsync(
                    new BsonDocument(
                        "_id",
                        ObjectId.Parse(fixture.PayloadId)),
                    Builders<BsonDocument>.Update
                        .Set("payloadRevision", 1)
                        .Set("values1DJson", emptyValuesJson)
                        .Set("fieldValuesJson", emptyFieldValuesJson)
                        .Set("tableValuesRootJson", emptyTableValuesJson)
                        .Set("summarySourceJson", emptySummaryJson)
                        .Set("payloadHash", initialPayloadHash)
                        .Set("payloadSizeBytes", initialPayloadSize)
                        .Set("status", WorkReportPayloadStatus.Ready)
                        .Set("updatedAtUtc", now)
                        .Set("updatedByUserId", ObjectId.Parse(executor.Id))
                        .Set("isDeleted", false),
                    cancellationToken: ct),
            "P10 production DIRECT payload reset");

        var reportUpdate = Builders<BsonDocument>.Update
            .Set("status", 0)
            .Set("payloadRevision", 1)
            .Set("payloadHash", initialPayloadHash)
            .Set("payloadSizeBytes", initialPayloadSize)
            .Set("payloadStatus", WorkReportPayloadStatus.Ready)
            .Set("values1DJson", emptyValuesJson)
            .Set("fieldValuesJson", emptyFieldValuesJson)
            .Set("tableValuesRootJson", emptyTableValuesJson)
            .Set("summarySourceJson", emptySummaryJson)
            .Set("dynamicFormSchemaHash", publishedSchema.Sha256)
            .Set("lifecycleRevision", 1)
            .Set("isCurrent", true)
            .Set("isActive", true)
            .Set("isDeleted", false)
            .Set(
                "cumulativeContributionMode",
                DynamicFlowContributionPolicyContract.Include)
            .Set("lifecycleProjectionOutbox", new BsonArray())
            .Set("lifecycleProjectionLastCompletedRevision", 0)
            .Set("lifecycleProjectionClaimToken", BsonNull.Value)
            .Set("lifecycleProjectionClaimedAtUtc", BsonNull.Value)
            .Set("lifecycleProjectionClaimExpiresAtUtc", BsonNull.Value)
            .Set("lifecycleProjectionLastError", BsonNull.Value)
            .Set("updatedAtUtc", now)
            .Set("updatedByUserId", ObjectId.Parse(executor.Id))
            .Unset("submittedAtUtc")
            .Unset("submittedByUserId")
            .Unset("approvedAtUtc")
            .Unset("approvedByUserId")
            .Unset("payloadMutationCommandId")
            .Unset("payloadMutationCommandHash")
            .Unset("payloadMutationOperation")
            .Unset("payloadMutationStartedAtUtc")
            .Unset("lastPayloadCommandId")
            .Unset("lastPayloadCommandHash")
            .Unset("lastPayloadCommandOperation")
            .Unset("lastPayloadCommandRevision")
            .Unset("dynamicFlowMappingReceiptId")
            .Unset("dynamicFlowMappingProvenanceId")
            .Unset("dynamicFlowMappingProvenanceHash")
            .Unset("dynamicFlowMappingResultPayloadRevision")
            .Unset("dynamicFlowMappingResultPayloadHash")
            .Unset("lastLifecycleCommandId")
            .Unset("lastLifecycleCommandHash")
            .Unset("lastLifecycleCommandOperation")
            .Unset("lastLifecycleCommandRevision")
            .Unset("lastLifecycleCommandPayloadRevision")
            .Unset("lastLifecycleCommandStatus")
            .Unset("lastLifecycleCommandIsActive");
        if (useNativeNullPeriodPair)
        {
            reportUpdate = reportUpdate
                .Unset("periodStart")
                .Unset("periodEnd");
        }
        await P10ProductionRequireMatchedAsync(
            database.GetCollection<BsonDocument>("work_assignment_report")
                .UpdateOneAsync(
                    new BsonDocument(
                        "_id",
                        ObjectId.Parse(fixture.ReportId)),
                    reportUpdate,
                    cancellationToken: ct),
            "P10 production DIRECT report reset");

        var periodUpdate = Builders<BsonDocument>.Update
            .Set("currentReportId", ObjectId.Parse(fixture.ReportId))
            .Set("sourceLifecycleReportId", ObjectId.Parse(fixture.ReportId))
            .Set("sourceLifecycleRevision", 1)
            .Set("dynamicFormSchemaHash", publishedSchema.Sha256)
            .Set("assigneeUserId", ObjectId.Parse(executor.Id))
            .Set("assigneeUnitId", ObjectId.Parse(fixture.UnitAId))
            .Set("isActive", true)
            .Set("updatedAtUtc", now)
            .Set("updatedByUserId", ObjectId.Parse(admin.Id));
        if (useNativeNullPeriodPair)
        {
            periodUpdate = periodUpdate
                .Unset("periodStart")
                .Unset("periodEnd");
        }
        await P10ProductionRequireMatchedAsync(
            database.GetCollection<BsonDocument>("work_report_periods")
                .UpdateOneAsync(
                    new BsonDocument(
                        "_id",
                        ObjectId.Parse(fixture.ReportPeriodId)),
                    periodUpdate,
                    cancellationToken: ct),
            "P10 production DIRECT report period");

        if (useNativeNullPeriodPair)
        {
            var preparedReport = await LoadP10ProductionReportAsync(
                fixture.ReportId,
                ct);
            var preparedPeriod = await database
                .GetCollection<BsonDocument>("work_report_periods")
                .Find(new BsonDocument(
                    "_id",
                    ObjectId.Parse(fixture.ReportPeriodId)))
                .SingleAsync(ct);
            RequireP10ProductionNullPeriodPair(
                preparedReport,
                "P10 production DIRECT prepared report");
            RequireP10ProductionNullPeriodPair(
                preparedPeriod,
                "P10 production DIRECT prepared period");
        }

        await P10ProductionRequireMatchedAsync(
            database.GetCollection<BsonDocument>(
                    "dynamic_flow_step_instances")
                .UpdateOneAsync(
                    new BsonDocument(
                        "_id",
                        ObjectId.Parse(fixture.FlowStepInstanceId)),
                    Builders<BsonDocument>.Update
                        .Set("definitionRevision", canonicalFlow.PayloadHash)
                        .Set("state", "ASSIGNED")
                        .Set("reportLifecycleRevision", 1)
                        .Set("reportLifecycleStatus", "DRAFT")
                        .Set("reportLifecycleIsActive", true)
                        .Set("reportId", ObjectId.Parse(fixture.ReportId))
                        .Set("formSchemaHash", publishedSchema.Sha256)
                        .Set("formSnapshotHash", publishedSchema.Sha256)
                        .Set("isCanonicalEpoch", true)
                        .Set("invalidatedByFlowEventId", BsonNull.Value)
                        .Set("supersededByStepInstanceId", BsonNull.Value)
                        .Set("updatedAtUtc", now)
                        .Set("updatedByUserId", ObjectId.Parse(admin.Id)),
                    cancellationToken: ct),
            "P10 production DIRECT flow step");

        const string emptyLabelConfigHash =
            "74234e98afe7498fb5daf1f36ac2d78acc339464f950703b8c019892f982b90b";
        var labelResponse = await RequireApi().PostAsync(
            "api/labels/config",
            new JsonObject
            {
                ["commandId"] = "p10-closeout-direct-label-001",
                ["expectedRevision"] = 0,
                ["expectedConfigHash"] = emptyLabelConfigHash,
                ["payload"] = new JsonObject
                {
                    ["code"] = P10ProductionDirectFieldKey,
                    ["name"] = "P10 closeout amount",
                    ["description"] = "P10 closeout DIRECT statistic label",
                    ["color"] = "#336699",
                    ["groupCode"] = "p10",
                    ["usage"] = "STATISTIC",
                    ["dataType"] = "NUMBER",
                    ["valueSourceType"] = "NONE",
                    ["valueOptions"] = new JsonArray(),
                    ["valueSourceCatalogId"] = null,
                    ["scopeType"] = "GLOBAL",
                    ["scopeId"] = null,
                    ["isActive"] = true
                }
            },
            admin.Token,
            ct: ct);
        P10ProductionExpectSuccess(
            labelResponse,
            "P10 production DIRECT amount label config");
        var labelOwner = await database
            .GetCollection<BsonDocument>("labels")
            .Find(new BsonDocument
            {
                ["code"] = P10ProductionDirectFieldKey,
                ["isDeleted"] = false
            })
            .SingleAsync(ct);
        RegisterP10ProductionCleanup(
            ownedCleanup,
            "labels",
            P10ProductionRequiredBsonObjectIdString(labelOwner, "_id"));

        if (includeSecondStatisticField)
        {
            var secondLabelResponse = await RequireApi().PostAsync(
                "api/labels/config",
                new JsonObject
                {
                    ["commandId"] =
                        "p10-closeout-direct-label-secondary-001",
                    ["expectedRevision"] = 0,
                    ["expectedConfigHash"] = emptyLabelConfigHash,
                    ["payload"] = new JsonObject
                    {
                        ["code"] = P10ProductionDirectSecondFieldKey,
                        ["name"] = "P10 closeout secondary amount",
                        ["description"] =
                            "P10 closeout DIRECT secondary statistic label",
                        ["color"] = "#663399",
                        ["groupCode"] = "p10",
                        ["usage"] = "STATISTIC",
                        ["dataType"] = "NUMBER",
                        ["valueSourceType"] = "NONE",
                        ["valueOptions"] = new JsonArray(),
                        ["valueSourceCatalogId"] = null,
                        ["scopeType"] = "GLOBAL",
                        ["scopeId"] = null,
                        ["isActive"] = true
                    }
                },
                admin.Token,
                ct: ct);
            P10ProductionExpectSuccess(
                secondLabelResponse,
                "P10 production DIRECT secondary amount label config");
            var secondLabelOwner = await database
                .GetCollection<BsonDocument>("labels")
                .Find(new BsonDocument
                {
                    ["code"] = P10ProductionDirectSecondFieldKey,
                    ["isDeleted"] = false
                })
                .SingleAsync(ct);
            RegisterP10ProductionCleanup(
                ownedCleanup,
                "labels",
                P10ProductionRequiredBsonObjectIdString(
                    secondLabelOwner,
                    "_id"));
        }

        var initialConfig = await RequireApi().GetAsync(
            $"api/dynamic-forms/{fixture.DynamicFormVersionId}/statistics",
            admin.Token,
            ct: ct);
        P10ProductionExpectSuccess(
            initialConfig,
            "P10 production DIRECT initial statistic config");
        var initialConfigObject = ApiHarnessClient.RequiredObject(
            initialConfig.Json,
            "P10 production DIRECT initial statistic config");
        var statisticFields = new JsonArray
        {
            new JsonObject
            {
                ["fieldId"] = P10ProductionDirectFieldId,
                ["isStatistic"] = true,
                ["statisticLabelCodes"] = new JsonArray
                {
                    P10ProductionDirectFieldKey
                },
                ["statistic"] = new JsonObject
                {
                    ["aggregateOps"] = includeSecondStatisticField
                        ? new JsonArray { "COUNT", "SUM" }
                        : new JsonArray
                        {
                            "COUNT", "SUM", "AVG", "MIN", "MAX"
                        },
                    ["bucketMode"] = "NONE",
                    ["showInDetail"] = true,
                    ["showInTree"] = true
                }
            }
        };
        if (includeSecondStatisticField)
        {
            statisticFields.Add(new JsonObject
            {
                ["fieldId"] = P10ProductionDirectSecondFieldId,
                ["isStatistic"] = true,
                ["statisticLabelCodes"] = new JsonArray
                {
                    P10ProductionDirectSecondFieldKey
                },
                ["statistic"] = new JsonObject
                {
                    ["aggregateOps"] = new JsonArray
                    {
                        "COUNT", "SUM"
                    },
                    ["bucketMode"] = "NONE",
                    ["showInDetail"] = true,
                    ["showInTree"] = true
                }
            });
        }

        var persistedConfig = await RequireApi().PatchAsync(
            $"api/dynamic-forms/{fixture.DynamicFormVersionId}/statistics",
            new JsonObject
            {
                ["commandId"] = "p10-closeout-direct-config-001",
                ["expectedRevision"] = P10ProductionRequiredJsonLong(
                    initialConfigObject,
                    "revision"),
                ["expectedConfigHash"] = ApiHarnessClient.RequiredString(
                    initialConfigObject,
                    "configHash"),
                ["payload"] = new JsonObject
                {
                    ["fields"] = statisticFields
                }
            },
            admin.Token,
            ct: ct);
        P10ProductionExpectSuccess(
            persistedConfig,
            "P10 production DIRECT locked statistic config");
        var config = ApiHarnessClient.RequiredObject(
            persistedConfig.Json,
            "P10 production DIRECT locked statistic config");
        HarnessAssert.Equal(
            "LOCKED",
            ApiHarnessClient.RequiredString(config, "status"),
            "P10 production DIRECT config status");

        var owner = await templates
            .Find(new BsonDocument(
                "_id",
                ObjectId.Parse(fixture.DynamicFormVersionId)))
            .SingleAsync(ct);
        var sections = owner
            .GetValue("statisticConfigSections", new BsonDocument())
            .AsBsonDocument;
        var fieldConfig = JsonNode.Parse(
            sections.GetValue("fieldSectionJson", "[]").AsString)
            ?? new JsonArray();
        var tableConfig = JsonNode.Parse(
            sections.GetValue("tableSectionJson", "[]").AsString)
            ?? new JsonArray();
        var dependencyPins = owner
            .GetValue("statisticConfigDependencyPins", new BsonArray())
            .AsBsonArray
            .Select(pin => pin.AsString)
            .ToArray();
        var configPayload = new JsonObject
        {
            ["ownerKind"] = "DYNAMIC_FORM",
            ["ownerId"] = fixture.DynamicFormVersionId,
            ["fieldConfig"] = fieldConfig,
            ["tableConfig"] = tableConfig,
            ["dependencyPins"] = new JsonArray(
                dependencyPins
                    .Select(pin => JsonValue.Create(pin))
                    .ToArray())
        };
        var configHash =
            StatisticReconciliationCanonicalJson.HashObject(configPayload);
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredString(config, "configHash"),
            configHash,
            "P10 production DIRECT config hash recompute");
        HarnessAssert.Equal(
            owner.GetValue("statisticConfigHash", string.Empty).AsString,
            configHash,
            "P10 production DIRECT owner config hash");

        var configId = ApiHarnessClient.RequiredString(config, "configId");
        var configVersionId = ApiHarnessClient.RequiredString(
            config,
            "versionId");
        var configVersionNo = ApiHarnessClient.RequiredInt(
            config,
            "versionNo");
        var configRevision = P10ProductionRequiredJsonLong(
            config,
            "revision");
        var configBundleHash = StatisticReconciliationCanonicalJson.HashObject(
            new
            {
                schema = "P10_P8_CONFIG_BUNDLE_PIN_V1",
                ownerId = fixture.DynamicFormVersionId,
                configId,
                configVersionId,
                configVersionNo,
                configRevision,
                configHash
            });
        return new P10ProductionSchemaConfigPins(
            publishedSchema.Sha256,
            canonicalFlow.PayloadHash,
            flowVersion.Id,
            includeVersion.Id,
            includeVersion.VersionNo,
            3,
            configId,
            configVersionId,
            configVersionNo,
            configRevision,
            configHash,
            configBundleHash);
    }

    private async Task<P10ProductionMappingPins>
        SeedP10CanonicalP7DirectMappingLineageAsync(
            P10Fixture fixture,
            List<P10CleanupHandle> ownedCleanup,
            string? mappingFlowVersionId,
            string? cumulativeContributionPolicyJson,
            CancellationToken ct)
    {
        var database = RequireDatabase();
        var reportCollection = database.GetCollection<WorkAssignmentReport>(
            "work_assignment_report");
        var report = await reportCollection
            .Find(item => item.Id == fixture.ReportId && !item.IsDeleted)
            .SingleAsync(ct);
        var payloadCollection = database.GetCollection<WorkReportPayload>(
            "work_report_payloads");
        var payload = await payloadCollection
            .Find(item => item.ReportId == report.Id && !item.IsDeleted)
            .SingleAsync(ct);
        var flowVersion = await database
            .GetCollection<DynamicFlowTemplateVersion>(
                "dynamic_flow_template_versions")
            .Find(version =>
                version.Id == (mappingFlowVersionId ?? fixture.FlowTemplateVersionId) &&
                !version.IsDeleted)
            .SingleAsync(ct);
        DynamicFlowContributionPolicyContract.ValidateLockedPolicy(flowVersion);
        var flowStep = await database
            .GetCollection<BsonDocument>("dynamic_flow_step_instances")
            .Find(new BsonDocument(
                "_id",
                ObjectId.Parse(fixture.FlowStepInstanceId)))
            .SingleAsync(ct);

        const string commandId = "p10-closeout-direct-p7-map-001";
        var now = DateTime.UtcNow;
        var nextPayloadRevision = report.PayloadRevision + 1;
        var mappingSummary = new JsonObject
        {
            ["kind"] = "DYNAMIC_FLOW_MAPPING",
            ["schemaVersion"] = "P7-MAP-RESULT-1",
            ["sourceSignature"] = StatRunCanonicalJson.HashText(
                $"P10-CLOSEOUT-DIRECT-SOURCE\n{report.Id}\n{report.PayloadHash}"),
            ["result"] = "P10 closeout canonical DIRECT amount mapping"
        }.ToJsonString();
        var resultPayloadHash = ComputeP10ProductionPayloadHash(
            payload.Values1DJson,
            payload.FieldValuesJson ?? "{}",
            payload.TableValuesRootJson ?? "{\"blocks\":[]}",
            mappingSummary);
        var receiptId = ObjectId.GenerateNewId().ToString();
        var provenanceId = ObjectId.GenerateNewId().ToString();
        var eventId = ObjectId.GenerateNewId().ToString();
        var outboxId = ObjectId.GenerateNewId().ToString();
        RegisterP10ProductionCleanup(
            ownedCleanup,
            "dynamic_flow_mapping_apply_receipts",
            receiptId);
        RegisterP10ProductionCleanup(
            ownedCleanup,
            "dynamic_flow_mapping_provenance",
            provenanceId);
        RegisterP10ProductionCleanup(
            ownedCleanup,
            "dynamic_flow_mapping_events",
            eventId);
        RegisterP10ProductionCleanup(
            ownedCleanup,
            "dynamic_flow_mapping_outbox",
            outboxId);

        var mappingRuleSetHash = StatRunCanonicalJson.HashText(
            $"P10-CLOSEOUT-DIRECT-MAPPING-RULESET-V1\n{flowVersion.PayloadHash}");
        var sourceSignature = StatRunCanonicalJson.HashText(
            $"P10-CLOSEOUT-DIRECT-MAPPING-SOURCE-V1\n{report.Id}\n{report.PayloadRevision}\n{report.PayloadHash}");
        var semanticProof = BuildP10ProductionMappingSemanticProof(
            report,
            payload,
            mappingSummary,
            cumulativeContributionPolicyJson,
            resultPayloadHash);
        var resultSemanticHash = semanticProof.ResultSemanticHash;
        var contributionPolicyHash =
            semanticProof.ContributionPolicyHash;
        var runtimePin = new DynamicFlowMappingRuntimePin
        {
            FlowFamilyId = fixture.FlowTemplateId,
            FlowVersionId = flowVersion.Id,
            FlowVersionNo = flowVersion.VersionNo,
            FlowPayloadHash = flowVersion.PayloadHash,
            CatalogVersion = flowVersion.CatalogVersion,
            CatalogSemanticHash = flowVersion.CatalogSemanticHash,
            MappingRuleSetHash = mappingRuleSetHash,
            EvaluatorVersion = "P7-MAP-EVALUATOR-1",
            FunctionRegistryVersion = "P7-MAP-FUNCTIONS-1",
            FunctionRegistryHash = StatRunCanonicalJson.HashText(
                "P7-MAP-FUNCTIONS-1"),
            FlowInstanceId = fixture.FlowInstanceId,
            ExecutionEpoch = flowStep.GetValue("executionEpoch").ToInt32(),
            StepInstanceId = fixture.FlowStepInstanceId,
            StepId = P10ProductionRequiredBsonString(flowStep, "flowStepId"),
            BranchId = P10ProductionRequiredBsonObjectIdString(
                flowStep,
                "branchId"),
            AttemptNo = flowStep.GetValue("attemptNo").ToInt32(),
            FormFamilyId = fixture.DynamicFormFamilyId,
            FormVersionId = fixture.DynamicFormVersionId,
            FormVersionNo = report.DynamicFormVersionNo ?? 1,
            FormSchemaHash = P10ProductionRequiredPin(
                report.DynamicFormSchemaHash,
                "report.dynamicFormSchemaHash"),
            FormSnapshotHash = P10ProductionRequiredPin(
                report.DynamicFormSchemaHash,
                "report.dynamicFormSchemaHash")
        };
        var resultSnapshot = new BsonDocument
        {
            ["schemaVersion"] = "P7-MAP-RESULT-1",
            ["targetReportId"] = ObjectId.Parse(report.Id),
            ["targetPayloadRevision"] = nextPayloadRevision,
            ["targetPayloadHash"] = resultPayloadHash,
            ["summarySourceJson"] = mappingSummary,
            ["resultSemanticHash"] = resultSemanticHash,
            ["contributionPolicyHash"] = contributionPolicyHash
        };
        var resultSnapshotHash =
            DynamicFlowMappingLifecycleContract.ComputeDocumentHash(
                resultSnapshot);
        var provenanceSnapshot = new BsonDocument
        {
            ["schemaVersion"] = "P7-MAP-PROVENANCE-1",
            ["receiptId"] = ObjectId.Parse(receiptId),
            ["targetReportId"] = ObjectId.Parse(report.Id),
            ["mappingRuleSetHash"] = mappingRuleSetHash,
            ["sourceSignature"] = sourceSignature,
            ["resultSemanticHash"] = resultSemanticHash,
            ["resultSnapshotHash"] = resultSnapshotHash
        };
        var provenanceHash =
            DynamicFlowMappingLifecycleContract.ComputeDocumentHash(
                provenanceSnapshot);
        var eventPayload = new BsonDocument
        {
            ["schemaVersion"] = "P7-MAP-EVENT-1",
            ["receiptId"] = ObjectId.Parse(receiptId),
            ["provenanceId"] = ObjectId.Parse(provenanceId),
            ["provenanceHash"] = provenanceHash,
            ["resultPayloadHash"] = resultPayloadHash
        };
        var eventPayloadHash =
            DynamicFlowMappingLifecycleContract.ComputeDocumentHash(
                eventPayload);
        var eventKey =
            DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(new
            {
                operation =
                    DynamicFlowMappingLifecycleContract.ApplyInitialOperation,
                receiptId,
                provenanceId,
                targetReportId = report.Id,
                resultPayloadHash
            });
        var actorUserId = Actor("executor").Id;
        var intent = new DynamicFlowMappingReconcileIntent
        {
            ActorUserId = actorUserId,
            ReceiptId = receiptId,
            ProvenanceId = provenanceId,
            EventId = eventId,
            TargetReportId = report.Id,
            TargetAssignmentId = report.WorkAssignmentId,
            CommandId = commandId,
            TargetPayloadRevision = nextPayloadRevision,
            TargetPayloadHash = resultPayloadHash,
            TargetLifecycleRevision = report.LifecycleRevision,
            SourceSignatureVersion = "P7-MAP-SOURCE-SIGNATURE-1",
            SourceSignature = sourceSignature,
            ResultSemanticHash = resultSemanticHash,
            MappingRuleSetHash = mappingRuleSetHash,
            ProvenanceHash = provenanceHash,
            RuntimePin = runtimePin,
            ProjectionSnapshot = resultSnapshot,
            ProjectionSnapshotHash = resultSnapshotHash,
            AuditSnapshot = provenanceSnapshot,
            AuditSnapshotHash = provenanceHash,
            ProjectionBusinessKeys = [report.Id],
            CommittedAtUtc = now
        };
        var intentHash =
            DynamicFlowMappingLifecycleContract.ComputeDocumentHash(
                intent.ToBsonDocument());
        var outboxDedupeKey =
            DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(new
            {
                operation =
                    DynamicFlowMappingLifecycleContract.ApplyInitialOperation,
                eventKey
            });
        var mappingEvent = new DynamicFlowMappingEvent
        {
            Id = eventId,
            EventKey = eventKey,
            EventType = "DYNAMIC_FLOW_MAPPING_APPLIED",
            ReceiptId = receiptId,
            ProvenanceId = provenanceId,
            TargetReportId = report.Id,
            TargetAssignmentId = report.WorkAssignmentId,
            CommandId = commandId,
            CorrelationId = commandId,
            RuntimePin = runtimePin,
            TargetPayloadRevision = nextPayloadRevision,
            TargetPayloadHash = resultPayloadHash,
            TargetLifecycleRevision = report.LifecycleRevision,
            SourceSignatureVersion = "P7-MAP-SOURCE-SIGNATURE-1",
            SourceSignature = sourceSignature,
            ResultSemanticHash = resultSemanticHash,
            ProvenanceHash = provenanceHash,
            Payload = eventPayload,
            PayloadHash = eventPayloadHash,
            ActorUserId = actorUserId,
            OccurredAtUtc = now
        };
        var outbox = new DynamicFlowMappingOutboxItem
        {
            Id = outboxId,
            ReceiptId = receiptId,
            EventId = eventId,
            ProvenanceId = provenanceId,
            TargetReportId = report.Id,
            CommandId = commandId,
            Operation =
                DynamicFlowMappingLifecycleContract.ApplyInitialOperation,
            DedupeKey = outboxDedupeKey,
            Intent = intent,
            IntentHash = intentHash,
            State = DynamicFlowMappingOutboxStates.Reconciled,
            AttemptCount = 1,
            RepairEpoch = 0,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ReconciledAtUtc = now
        };
        var provenance = new DynamicFlowMappingProvenanceRecord
        {
            Id = provenanceId,
            ReceiptId = receiptId,
            WorkId = report.WorkId,
            TargetAssignmentId = report.WorkAssignmentId,
            TargetReportId = report.Id,
            CommandId = commandId,
            TargetPayloadRevision = nextPayloadRevision,
            TargetPayloadHash = resultPayloadHash,
            TargetLifecycleRevision = report.LifecycleRevision,
            SourceSignatureVersion = "P7-MAP-SOURCE-SIGNATURE-1",
            SourceSignature = sourceSignature,
            ResultSemanticHash = resultSemanticHash,
            MappingRuleSetHash = mappingRuleSetHash,
            RuntimePin = runtimePin,
            SourcePins = [],
            ResultSnapshot = resultSnapshot,
            ResultSnapshotHash = resultSnapshotHash,
            ProvenanceSnapshot = provenanceSnapshot,
            ProvenanceHash = provenanceHash,
            OwnedTargetRefs = ["FIELD:amount"],
            State = DynamicFlowMappingProvenanceStates.Current,
            CreatedByUserId = actorUserId,
            CreatedAtUtc = now
        };
        var writeSetHash =
            DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(new
            {
                receiptId,
                provenanceId,
                eventId,
                eventKey,
                eventPayloadHash,
                outboxId,
                outboxDedupeKey,
                intentHash,
                targetReportId = report.Id,
                resultPayloadRevision = nextPayloadRevision,
                resultPayloadHash,
                resultLifecycleRevision = report.LifecycleRevision,
                provenanceHash
            });
        var receipt = new DynamicFlowMappingApplyReceipt
        {
            Id = receiptId,
            WorkId = report.WorkId,
            TargetAssignmentId = report.WorkAssignmentId,
            TargetReportId = report.Id,
            CommandId = commandId,
            RequestHash =
                DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(new
                {
                    commandId,
                    report.Id,
                    nextPayloadRevision,
                    mappingRuleSetHash
                }),
            PreviewTokenId = "p10-closeout-direct-signed-preview-001",
            PreviewTokenHash =
                DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(new
                {
                    preview = "p10-closeout-direct-signed-preview-001",
                    report.Id,
                    mappingRuleSetHash
                }),
            PreviewIssuedAtUtc = now.AddMinutes(-1),
            PreviewExpiresAtUtc = now.AddMinutes(29),
            SourceSignatureVersion = "P7-MAP-SOURCE-SIGNATURE-1",
            SourceSignature = sourceSignature,
            ResultSemanticHash = resultSemanticHash,
            AuthorizationSnapshotHash =
                DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(new
                {
                    actorUserId,
                    report.WorkId,
                    report.WorkAssignmentId
                }),
            ExpectedTargetPayloadRevision = report.PayloadRevision,
            ExpectedTargetPayloadHash = P10ProductionRequiredPin(
                report.PayloadHash,
                "report.payloadHash"),
            ExpectedTargetLifecycleRevision = report.LifecycleRevision,
            ExpectedTargetStatus = report.Status.ToString().ToUpperInvariant(),
            ExpectedTargetIsActive = report.IsActive,
            RuntimePin = runtimePin,
            SourcePins = [],
            State = DynamicFlowMappingApplyStates.Committed,
            ResultSnapshot = resultSnapshot,
            ResultSnapshotHash = resultSnapshotHash,
            WriteSetHash = writeSetHash,
            ResultPayloadRevision = nextPayloadRevision,
            ResultPayloadHash = resultPayloadHash,
            ResultLifecycleRevision = report.LifecycleRevision,
            ProvenanceId = provenanceId,
            ProvenanceHash = provenanceHash,
            EventId = eventId,
            OutboxIntentId = outboxId,
            ActorUserId = actorUserId,
            CreatedAtUtc = now,
            CommittedAtUtc = now,
            UpdatedAtUtc = now,
            ReconciledAtUtc = now
        };

        await database.GetCollection<DynamicFlowMappingApplyReceipt>(
                "dynamic_flow_mapping_apply_receipts")
            .InsertOneAsync(receipt, cancellationToken: ct);
        await database.GetCollection<DynamicFlowMappingProvenanceRecord>(
                "dynamic_flow_mapping_provenance")
            .InsertOneAsync(provenance, cancellationToken: ct);
        await database.GetCollection<DynamicFlowMappingEvent>(
                "dynamic_flow_mapping_events")
            .InsertOneAsync(mappingEvent, cancellationToken: ct);
        await database.GetCollection<DynamicFlowMappingOutboxItem>(
                "dynamic_flow_mapping_outbox")
            .InsertOneAsync(outbox, cancellationToken: ct);

        payload.PayloadRevision = nextPayloadRevision;
        payload.SummarySourceJson = mappingSummary;
        payload.PayloadHash = resultPayloadHash;
        payload.PayloadSizeBytes = P10ProductionPayloadSize(
            payload.Values1DJson,
            payload.FieldValuesJson ?? string.Empty,
            payload.TableValuesRootJson ?? string.Empty,
            mappingSummary);
        payload.UpdatedAtUtc = now;
        payload.UpdatedByUserId = actorUserId;
        await payloadCollection.ReplaceOneAsync(
            item => item.Id == payload.Id,
            payload,
            cancellationToken: ct);

        report.PayloadRevision = nextPayloadRevision;
        report.PayloadHash = resultPayloadHash;
        report.PayloadSizeBytes = payload.PayloadSizeBytes;
        report.SummarySourceJson = mappingSummary;
        report.DataOrigin =
            string.IsNullOrWhiteSpace(cumulativeContributionPolicyJson)
                ? report.DataOrigin
                : WorkReportDataOrigin.PartialMapping;
        report.CumulativeContributionMode =
            string.IsNullOrWhiteSpace(cumulativeContributionPolicyJson)
                ? P10ProductionRequiredPin(
                    flowVersion.ContributionPolicy,
                    "flowVersion.contributionPolicy")
                : WorkReportCumulativeContributionMode.Exclude;
        report.CumulativeContributionPolicyJson =
            cumulativeContributionPolicyJson;
        report.DynamicFlowMappingReceiptId = receiptId;
        report.DynamicFlowMappingProvenanceId = provenanceId;
        report.DynamicFlowMappingProvenanceHash = provenanceHash;
        report.DynamicFlowMappingResultPayloadRevision = nextPayloadRevision;
        report.DynamicFlowMappingResultPayloadHash = resultPayloadHash;
        report.UpdatedAtUtc = now;
        report.UpdatedByUserId = actorUserId;
        await reportCollection.ReplaceOneAsync(
            item => item.Id == report.Id,
            report,
            cancellationToken: ct);

        return new P10ProductionMappingPins(
            receiptId,
            provenanceId,
            provenanceHash,
            eventId,
            outboxId,
            resultPayloadHash,
            nextPayloadRevision,
            resultSemanticHash,
            resultSnapshotHash,
            contributionPolicyHash);
    }

    private static P10ProductionMappingSemanticProof
        BuildP10ProductionMappingSemanticProof(
            WorkAssignmentReport report,
            WorkReportPayload payload,
            string mappingSummary,
            string? cumulativeContributionPolicyJson,
            string resultPayloadHash)
    {
        var contributionPolicyHash =
            string.IsNullOrWhiteSpace(cumulativeContributionPolicyJson)
                ? StatRunCanonicalJson.HashText(string.Empty)
                : DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(
                    cumulativeContributionPolicyJson);
        if (string.IsNullOrWhiteSpace(cumulativeContributionPolicyJson))
        {
            return new P10ProductionMappingSemanticProof(
                StatRunCanonicalJson.HashText(
                    $"P10-CLOSEOUT-DIRECT-MAPPING-RESULT-V1\n{resultPayloadHash}"),
                contributionPolicyHash);
        }

        var policy = JsonNode.Parse(cumulativeContributionPolicyJson)
            ?.AsObject()
            ?? throw new InvalidOperationException(
                "P10 production DIRECT mapping contribution policy must be an object.");
        var policyRules = policy["rules"]?.AsArray()
            ?? throw new InvalidOperationException(
                "P10 production DIRECT mapping contribution policy rules are missing.");
        var changes = policyRules
            .Select((node, index) =>
            {
                var rule = node?.AsObject()
                    ?? throw new InvalidOperationException(
                        $"P10 production DIRECT mapping contribution policy rule {index} must be an object.");
                var targetKind = rule["targetKind"]?.GetValue<string>()
                    ?? throw new InvalidOperationException(
                        $"P10 production DIRECT mapping contribution policy rule {index} targetKind is missing.");
                var targetKey = string.Equals(
                        targetKind,
                        "FIELD",
                        StringComparison.Ordinal)
                    ? rule["targetKey"]?.GetValue<string>()
                    : string.Join(
                        ':',
                        rule["blockId"]?.GetValue<string>(),
                        rule["columnKey"]?.GetValue<string>());
                return new DynamicFlowMappingChangeDto
                {
                    MappingId = rule["mappingId"]?.GetValue<string>()
                        ?? throw new InvalidOperationException(
                            $"P10 production DIRECT mapping contribution policy rule {index} mappingId is missing."),
                    MappingVersion = 1,
                    TargetKind = targetKind,
                    TargetKey = targetKey
                        ?? throw new InvalidOperationException(
                            $"P10 production DIRECT mapping contribution policy rule {index} target key is missing."),
                    PreviousValueJson = "10",
                    NextValueJson = "10",
                    Status = "UNCHANGED",
                    ContributionPolicy = rule["mode"]?.GetValue<string>()
                        ?? throw new InvalidOperationException(
                            $"P10 production DIRECT mapping contribution policy rule {index} mode is missing.")
                };
            })
            .ToList();
        var preview = new DynamicFlowMappingPreviewResponse
        {
            TargetReportId = report.Id,
            TargetAssignmentId = report.WorkAssignmentId,
            DataOrigin = WorkReportDataOrigin.PartialMapping,
            CumulativeContributionMode =
                WorkReportCumulativeContributionMode.Exclude,
            CumulativeContributionPolicyJson =
                cumulativeContributionPolicyJson,
            SummarySourceJson = mappingSummary,
            FieldValuesJson = payload.FieldValuesJson ?? "{}",
            TableValuesJson =
                payload.TableValuesRootJson ?? """{"blocks":[]}""",
            Changes = changes,
            HasBlockingConflicts = false
        };
        var resultSemanticHash =
            DynamicFlowMappingRuntimeContract.ComputeResultSemanticHash(
                preview);
        preview.CumulativeContributionPolicyJson = null;
        var policyUnboundSemanticHash =
            DynamicFlowMappingRuntimeContract.ComputeResultSemanticHash(
                preview);
        HarnessAssert.True(
            !string.Equals(
                resultSemanticHash,
                policyUnboundSemanticHash,
                StringComparison.Ordinal),
            "P10 production DIRECT result semantic hash must bind the mapping contribution policy.");

        return new P10ProductionMappingSemanticProof(
            resultSemanticHash,
            contributionPolicyHash);
    }
    private async Task<ApiHarnessResponse>
        ProcessP10ProductionLifecycleOutboxAsync(CancellationToken ct)
    {
        var response = await RequireApi().PostAsync(
            "api/admin/operations/job-runs/lifecycle-projection-outbox/process?maxReports=20",
            body: null,
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            "P10 production DIRECT lifecycle worker");
        return response;
    }

    private async Task<BsonDocument>
        DrainP10ProductionApprovePublicationAsync(
            string reportId,
            string commandId,
            CancellationToken ct)
    {
        BsonDocument? observed = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await ProcessP10ProductionLifecycleOutboxAsync(ct);
            var report = await LoadP10ProductionReportAsync(reportId, ct);
            observed = FindP10ProductionLifecycleEntry(
                report,
                "REVIEW_APPROVE",
                commandId);
            var state = P10ProductionNullableBsonString(observed, "state");
            var direct = P10ProductionNullableBsonString(
                observed,
                "directProjectionState");
            if (string.Equals(state, "COMPLETED", StringComparison.Ordinal) &&
                string.Equals(direct, "PUBLISHED", StringComparison.Ordinal))
            {
                return (BsonDocument)observed.DeepClone();
            }
        }

        throw new InvalidOperationException(
            "P10 production DIRECT approve publication did not converge. " +
            $"state={P10ProductionNullableBsonString(observed!, "state") ?? "missing"};" +
            $"direct={P10ProductionNullableBsonString(observed!, "directProjectionState") ?? "missing"};" +
            $"reason={P10ProductionNullableBsonString(observed!, "directProjectionReason") ?? "none"};" +
            $"error={P10ProductionNullableBsonString(observed!, "lastError") ?? "none"}");
    }

    private async Task<BsonDocument> RequireP10ProductionLifecycleEntryAsync(
        string reportId,
        string operation,
        string commandId,
        string expectedState,
        CancellationToken ct)
    {
        var report = await LoadP10ProductionReportAsync(reportId, ct);
        var entry = FindP10ProductionLifecycleEntry(
            report,
            operation,
            commandId);
        HarnessAssert.Equal(
            expectedState,
            P10ProductionRequiredBsonString(entry, "state"),
            $"P10 production DIRECT {operation} outbox state");
        foreach (var field in new[]
                 {
                     "directProjectionState",
                     "directProjectionReason",
                     "directProjectionRunId",
                     "directProjectionGenerationId",
                     "directProjectionGenerationHash",
                     "directProjectionCompletedAtUtc"
                 })
        {
            HarnessAssert.True(
                !entry.TryGetValue(field, out var value) || value.IsBsonNull,
                $"P10 production DIRECT {operation} unexpectedly populated {field}.");
        }
        return entry;
    }

    private async Task<BsonDocument> LoadP10ProductionReportAsync(
        string reportId,
        CancellationToken ct)
        => await RequireDatabase()
            .GetCollection<BsonDocument>("work_assignment_report")
            .Find(new BsonDocument("_id", ObjectId.Parse(reportId)))
            .SingleAsync(ct);

    private static BsonDocument FindP10ProductionLifecycleEntry(
        BsonDocument report,
        string operation,
        string commandId)
        => report
            .GetValue("lifecycleProjectionOutbox", new BsonArray())
            .AsBsonArray
            .Select(value => value.AsBsonDocument)
            .Single(entry =>
                string.Equals(
                    P10ProductionNullableBsonString(entry, "operation"),
                    operation,
                    StringComparison.Ordinal) &&
                string.Equals(
                    P10ProductionNullableBsonString(entry, "commandId"),
                    commandId,
                    StringComparison.Ordinal));

    private static void RequireP10ProductionPublishedJob(
        WorkReportStatisticRebuildJob job,
        P10Fixture fixture,
        P10ProductionSchemaConfigPins schema,
        P10ProductionMappingPins mapping,
        string expectedMappingFlowVersionId,
        bool requireNullPeriodPair = false)
    {
        HarnessAssert.Equal(
            WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection,
            job.RunKind,
            "P10 production DIRECT job kind");
        HarnessAssert.Equal(
            WorkReportStatisticRebuildJobStatuses.Completed,
            job.Status,
            "P10 production DIRECT job status");
        HarnessAssert.True(
            job.IsCurrentPublication,
            "P10 production DIRECT job must be current.");
        if (requireNullPeriodPair)
        {
            HarnessAssert.True(
                job.PeriodStartUtc is null && job.PeriodEndUtc is null,
                "P10 production DIRECT job must natively publish a null/null period pair.");
        }
        HarnessAssert.True(
            job.ReceiptResponseHash is null,
            "P10 production DIRECT lifecycle receipt must not carry a generic accepted-response hash.");
        HarnessAssert.Equal(
            fixture.ReportId,
            job.SourceReportId,
            "P10 production DIRECT source report");
        HarnessAssert.Equal(
            mapping.ResultPayloadRevision + 1,
            job.SourcePayloadRevision,
            "P10 production DIRECT mapped payload revision");
        HarnessAssert.Equal(
            mapping.ResultPayloadHash,
            job.SourcePayloadHash,
            "P10 production DIRECT mapped payload hash");
        HarnessAssert.Equal(
            schema.ConfigId,
            job.ConfigId,
            "P10 production DIRECT config id");
        HarnessAssert.Equal(
            schema.ConfigVersionId,
            job.ConfigVersionId,
            "P10 production DIRECT config version id");
        HarnessAssert.Equal(
            schema.ConfigVersionNo,
            job.ConfigVersionNo,
            "P10 production DIRECT config version no");
        HarnessAssert.Equal(
            schema.ConfigRevision,
            job.ConfigRevision,
            "P10 production DIRECT config revision");
        HarnessAssert.Equal(
            schema.ConfigHash,
            job.ConfigHash,
            "P10 production DIRECT config hash");
        HarnessAssert.Equal(
            schema.DynamicFormSchemaHash,
            job.DynamicFormSchemaHash,
            "P10 production DIRECT form schema hash");
        HarnessAssert.Equal(
            schema.FlowPayloadHash,
            job.FlowPayloadHash,
            "P10 production DIRECT flow payload hash");
        HarnessAssert.Equal(
            schema.FlowEffectiveVersionId,
            job.FlowTemplateVersionId,
            "P10 production DIRECT effective flow version id");
        HarnessAssert.Equal(
            schema.FlowEffectiveVersionNo,
            job.FlowTemplateVersionNo,
            "P10 production DIRECT effective flow version no");
        HarnessAssert.Equal(
            schema.FlowFamilyRevision,
            job.FlowFamilyRevision,
            "P10 production DIRECT flow family revision");
        HarnessAssert.Equal(
            schema.FlowOriginVersionId,
            job.FlowContributionOriginVersionId,
            "P10 production DIRECT contribution origin version id");
        HarnessAssert.True(
            !string.Equals(
                job.FlowTemplateVersionId,
                job.FlowContributionOriginVersionId,
                StringComparison.Ordinal),
            "P10 production DIRECT effective and origin versions must differ.");
        HarnessAssert.Equal(
            DynamicFlowContributionPolicyContract.Include,
            job.FlowContributionPolicy,
            "P10 production DIRECT contribution policy");
        HarnessAssert.True(
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                job.SourceMembershipSignature),
            "P10 production DIRECT membership signature");
        HarnessAssert.True(
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                job.FlowContributionPolicyHash),
            "P10 production DIRECT contribution policy hash");
        HarnessAssert.True(
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                job.FlowContributionLedgerHash),
            "P10 production DIRECT contribution ledger hash");
        HarnessAssert.True(
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                job.FlowContributionReversalBaselineHash),
            "P10 production DIRECT reversal baseline hash");
        HarnessAssert.Equal(
            1,
            job.FlowContributionSourceCount,
            "P10 production DIRECT flow contribution source count");
        HarnessAssert.Equal(
            0,
            job.NonFlowContributionSourceCount,
            "P10 production DIRECT non-flow contribution source count");
        var contributionSource = job.FlowContributionSources.Single();
        HarnessAssert.Equal(
            expectedMappingFlowVersionId,
            contributionSource.MappingFlowVersionId,
            "P10 production DIRECT mapping flow version");
        HarnessAssert.Equal(
            schema.FlowEffectiveVersionId,
            contributionSource.FlowTemplateVersionId,
            "P10 production DIRECT source effective version");
        HarnessAssert.True(
            job.DirectStoreDigests.Count == DirectStoreNames.Length,
            "P10 production DIRECT must publish all six store digests.");
        var digests = job.DirectStoreDigests.ToDictionary(
            digest => digest.Store,
            StringComparer.Ordinal);
        HarnessAssert.True(
            digests["work_report_field_stat_values"].RowCount > 0,
            "P10 production DIRECT must publish a field value row.");
        HarnessAssert.True(
            digests["work_report_field_stat_aggregates"].RowCount > 0,
            "P10 production DIRECT must publish a field aggregate row.");
        foreach (var zeroStore in new[]
                 {
                     "work_report_label_stat_aggregates",
                     "work_report_label_stat_values",
                     "work_report_table_stat_aggregates",
                     "work_report_table_stat_values"
                 })
        {
            HarnessAssert.Equal(
                0L,
                digests[zeroStore].RowCount,
                $"P10 production DIRECT {zeroStore} row count");
        }
    }

    private static void RequireP10ProductionNullPeriodPair(
        BsonDocument document,
        string context)
    {
        var startIsNull =
            !document.TryGetValue("periodStart", out var start) ||
            start.IsBsonNull;
        var endIsNull =
            !document.TryGetValue("periodEnd", out var end) ||
            end.IsBsonNull;
        HarnessAssert.True(
            startIsNull && endIsNull,
            $"{context} must carry a null/null period pair.");
    }

    private async Task RegisterP10ProductionDirectRowsAsync(
        string generationId,
        List<P10CleanupHandle> ownedCleanup,
        CancellationToken ct)
    {
        foreach (var collectionName in DirectStoreNames)
        {
            var ids = await RequireDatabase()
                .GetCollection<BsonDocument>(collectionName)
                .Find(new BsonDocument(
                    "directProjection.generationId",
                    generationId))
                .Project(new BsonDocument("_id", 1))
                .ToListAsync(ct);
            foreach (var row in ids)
            {
                RegisterP10ProductionCleanup(
                    ownedCleanup,
                    collectionName,
                    P10ProductionRequiredBsonObjectIdString(row, "_id"));
            }
        }
    }

    private void RegisterP10ProductionCleanup(
        List<P10CleanupHandle> ownedCleanup,
        string collection,
        string id)
    {
        var handle = new P10CleanupHandle(collection, id);
        if (!ownedCleanup.Contains(handle))
            ownedCleanup.Add(handle);
        if (!_cleanupHandles.Contains(handle))
            _cleanupHandles.Add(handle);
    }

    private static async Task P10ProductionRequireMatchedAsync(
        Task<UpdateResult> write,
        string context)
    {
        var result = await write;
        HarnessAssert.Equal(1L, result.MatchedCount, context);
    }

    private static string ComputeP10ProductionPayloadHash(
        string values1DJson,
        string? fieldValuesJson,
        string? tableRootJson,
        string? summarySourceJson)
        => HashText(
            new StringBuilder()
                .Append(values1DJson).Append('\n')
                .Append(fieldValuesJson).Append('\n')
                .Append(tableRootJson).Append('\n')
                .Append(summarySourceJson).Append('\n')
                .ToString());

    private static long P10ProductionPayloadSize(params string[] values)
        => values.Sum(value => Encoding.UTF8.GetByteCount(value));

    private static void P10ProductionExpectSuccess(
        ApiHarnessResponse response,
        string context)
    {
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted)
            return;
        throw new InvalidOperationException(
            $"{context} expected 200/202; actual={(int)response.StatusCode}; body={response.Body}");
    }

    private static long P10ProductionRequiredJsonLong(
        JsonNode? node,
        string property)
    {
        if (node is JsonObject obj &&
            obj[property] is JsonValue value &&
            value.TryGetValue<long>(out var number))
        {
            return number;
        }
        throw new InvalidOperationException(
            $"Response property '{property}' is missing or not an integer. Body={node?.ToJsonString() ?? "<null>"}");
    }

    private static string P10ProductionRequiredPin(
        string? value,
        string name)
        => !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                $"P10 production DIRECT pin '{name}' is missing.");

    private static int P10ProductionRequiredNullableInt(
        int? value,
        string name)
        => value ?? throw new InvalidOperationException(
            $"P10 production DIRECT pin '{name}' is missing.");

    private static long P10ProductionRequiredNullableLong(
        long? value,
        string name)
        => value ?? throw new InvalidOperationException(
            $"P10 production DIRECT pin '{name}' is missing.");

    private static string P10ProductionRequiredBsonString(
        BsonDocument document,
        string field)
        => P10ProductionNullableBsonString(document, field)
           ?? throw new InvalidOperationException(
               $"P10 production DIRECT BSON field '{field}' is missing.");

    private static string? P10ProductionNullableBsonString(
        BsonDocument document,
        string field)
    {
        if (!document.TryGetValue(field, out var value) || value.IsBsonNull)
            return null;
        return value.BsonType switch
        {
            BsonType.String => value.AsString,
            BsonType.ObjectId => value.AsObjectId.ToString(),
            _ => value.ToString()
        };
    }

    private static string P10ProductionRequiredBsonObjectIdString(
        BsonDocument document,
        string field)
    {
        var value = document.GetValue(field, BsonNull.Value);
        if (value.IsObjectId)
            return value.AsObjectId.ToString();
        if (value.IsString && ObjectId.TryParse(value.AsString, out _))
            return value.AsString;
        throw new InvalidOperationException(
            $"P10 production DIRECT BSON field '{field}' is not an ObjectId.");
    }

    private sealed record P10ProductionSchemaConfigPins(
        string DynamicFormSchemaHash,
        string FlowPayloadHash,
        string FlowOriginVersionId,
        string FlowEffectiveVersionId,
        int FlowEffectiveVersionNo,
        int FlowFamilyRevision,
        string ConfigId,
        string ConfigVersionId,
        int ConfigVersionNo,
        long ConfigRevision,
        string ConfigHash,
        string ConfigBundleHash);

    private sealed record P10ProductionMappingSemanticProof(
        string ResultSemanticHash,
        string ContributionPolicyHash);
    private sealed record P10ProductionMappingPins(
        string ReceiptId,
        string ProvenanceId,
        string ProvenanceHash,
        string EventId,
        string OutboxId,
        string ResultPayloadHash,
        int ResultPayloadRevision,
        string ResultSemanticHash,
        string ResultSnapshotHash,
        string ContributionPolicyHash);
}

internal sealed record P10ProductionDirectStoreDigestPin(
    string Store,
    long RowCount,
    string Sha256);

internal sealed record P10ProductionDirectFixturePins(
    string P9ResultId,
    string P9RunId,
    string P9GenerationId,
    string P9GenerationHash,
    long P9StateRevision,
    string P9StateHash,
    string P9FreshnessState,
    string RunKind,
    string RouteId,
    string CapabilityId,
    string ReceiptId,
    string CommandId,
    string RequestHash,
    string ImmutableHeaderHash,
    string ActorUserId,
    string TenantUnitId,
    string ScopeType,
    string ScopeId,
    string ScopeKind,
    string WorkId,
    string WorkAssignmentId,
    string SourceReportId,
    int SourcePayloadRevision,
    string SourcePayloadHash,
    int SourceLifecycleRevision,
    string SourceLifecycleEventKey,
    string SourceMembershipSignature,
    string SourceStatus,
    long DirectSourceRevision,
    long DirectPublicationRevision,
    string PublicationScopeKey,
    string ConfigId,
    string ConfigVersionId,
    int ConfigVersionNo,
    long ConfigRevision,
    string ConfigHash,
    string ConfigBundleHash,
    string CatalogVersion,
    string CatalogRawSha256,
    string CatalogSemanticSha256,
    string SchemaRawSha256,
    string SchemaSemanticSha256,
    string StageLockSha256,
    string CandidateChainId,
    string CandidatePromptId,
    string DynamicFormVersionId,
    string DynamicFormFamilyId,
    int DynamicFormVersionNo,
    string DynamicFormSchemaHash,
    string DynamicFormTemplateCode,
    string DynamicFormTemplateName,
    string FlowTemplateId,
    string FlowTemplateVersionId,
    int FlowTemplateVersionNo,
    int FlowFamilyRevision,
    string? FlowContributionOriginVersionId,
    string FlowInstanceId,
    long FlowInstanceRevision,
    string FlowInstanceState,
    string FlowEffectiveStatus,
    int FlowExecutionEpoch,
    string FlowExecutionEpochId,
    long FlowExecutionEpochRevision,
    string FlowExecutionEpochState,
    string FlowBranchId,
    string FlowStepId,
    int FlowAttemptNo,
    string FlowStepInstanceId,
    long FlowStepInstanceRevision,
    string FlowStepInstanceState,
    string FlowPayloadHash,
    string FlowCatalogVersion,
    string FlowCatalogSemanticHash,
    string FlowContributionPolicy,
    string FlowContributionPolicyHash,
    string FlowContributionWarning,
    string FlowContributionOperationVersion,
    string FlowContributionLedgerHash,
    string FlowContributionReversalBaselineHash,
    int FlowContributionSourceCount,
    int NonFlowContributionSourceCount,
    int FlowContributionTargetCount,
    string PeriodKey,
    string PeriodInstanceKey,
    string PeriodKind,
    DateTime? PeriodStartUtc,
    DateTime? PeriodEndUtc,
    string MappingReceiptId,
    string MappingProvenanceId,
    string MappingProvenanceHash,
    string MappingEventId,
    string MappingOutboxId,
    string MappingResultSemanticHash,
    string MappingResultSnapshotHash,
    string MappingContributionPolicyHash,
    IReadOnlyList<P10ProductionDirectStoreDigestPin> DirectStoreDigests,
    IReadOnlyList<P10CleanupHandle> OwnedCleanupHandles);
