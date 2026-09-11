namespace tdtd_be.Data.Indexes
{
    using MongoDB.Bson;
    using MongoDB.Driver;
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using tdtd_be.Data.Infrastructure;
    using tdtd_be.Models;
    using tdtd_be.Models.Statistics;
    using tdtd_be.Models.StatisticsConfiguration;
    using tdtd_be.Models.StatisticsReconciliation;

    public sealed record MongoIndexContractStatus(
        string Collection,
        string Name,
        BsonDocument Key,
        bool Unique,
        BsonDocument? PartialFilter,
        int? ExpireAfterSeconds,
        bool IsExact);

    public static class MongoIndexInitializer
    {
        public static async Task EnsureAsync(IMongoDatabase db, MongoOptions opt, CancellationToken ct = default)
        {
            // USERS
            var users = db.GetCollection<AppUser>(opt.UserCollection);
            await EnsureUsersAsync(users, ct);

            // REFRESH TOKENS
            var rts = db.GetCollection<RefreshTokenDoc>(opt.RefreshTokenCollection);
            await EnsureRefreshTokensAsync(rts, ct);

            // UNITS
            var units = db.GetCollection<Unit>(opt.UnitCollection);
            await EnsureUnitsAsync(units, ct);

            // UNIT TYPES
            var unitTypes = db.GetCollection<UnitType>(opt.UnitTypeCollection);
            await EnsureUnitTypesAsync(unitTypes, ct);

            // POSITIONS
            var positions = db.GetCollection<Position>(opt.PositionCollection);
            await EnsurePositionsAsync(positions, ct);

            // UNIT HISTORIES
            var unitHistories = db.GetCollection<UnitVersionHistory>(opt.UnitHistoryCollection);
            await EnsureUnitHistoriesAsync(unitHistories, ct);

            // FILES
            var files = db.GetCollection<FileDoc>(opt.FileDocCollection);
            await EnsureFilesAsync(files, ct);

            // DYNAMIC EXCEL
            var dx = db.GetCollection<DynamicExcelTemplate>(opt.DynamicExcelTemplateCollection);
            await EnsureDynamicExcelAsync(dx, ct);

            // DYNAMIC FORM
            var dynamicForms = db.GetCollection<DynamicFormTemplate>(opt.DynamicFormTemplateCollection);
            await EnsureDynamicFormsAsync(dynamicForms, ct);

            var dynamicFormSections = db.GetCollection<DynamicFormSectionDocument>(opt.DynamicFormSectionCollection);
            await EnsureDynamicFormSectionsAsync(dynamicFormSections, ct);

            // LABELS
            var labels = db.GetCollection<LabelCatalogItem>(opt.LabelCollection);
            await EnsureLabelsAsync(labels, ct);

            await EnsureStatConfigOperationsIndexesAsync(db, opt, ct);

            var labelEnumCatalogs = db.GetCollection<LabelEnumCatalog>(opt.LabelEnumCatalogCollection);
            await EnsureLabelEnumCatalogsAsync(labelEnumCatalogs, ct);

            var labelEnumOptionReadModels = db.GetCollection<LabelEnumOptionReadModel>(opt.LabelEnumOptionReadModelCollection);
            await EnsureLabelEnumOptionReadModelsAsync(labelEnumOptionReadModels, ct);

            // WORKS
            var works = db.GetCollection<Work>(opt.WorkCollection);
            await EnsureWorksAsync(works, ct);

            // DOC ROLE
            var docRole = db.GetCollection<DocRole>(opt.DocRoleCollection);
            await EnsureDocRolesAsync(docRole, ct);

            // DOC ROLE READ MODELS
            var workListDocRoles = db.GetCollection<WorkListDocRole>(opt.WorkListDocRoleCollection);
            await EnsureWorkListDocRolesAsync(workListDocRoles, ct);

            var assignmentListDocRoles = db.GetCollection<AssignmentListDocRole>(opt.AssignmentListDocRoleCollection);
            await EnsureAssignmentListDocRolesAsync(assignmentListDocRoles, ct);

            var myReportTemplateListDocRoles = db.GetCollection<MyReportTemplateListDocRole>(opt.MyReportTemplateListDocRoleCollection);
            await EnsureMyReportTemplateListDocRolesAsync(myReportTemplateListDocRoles, ct);

            var myReportPeriodListDocRoles = db.GetCollection<MyReportPeriodListDocRole>(opt.MyReportPeriodListDocRoleCollection);
            await EnsureMyReportPeriodListDocRolesAsync(myReportPeriodListDocRoles, ct);

            var reviewReportListDocRoles = db.GetCollection<ReviewReportListDocRole>(opt.ReviewReportListDocRoleCollection);
            await EnsureReviewReportListDocRolesAsync(reviewReportListDocRoles, ct);

            var reviewAssignmentSummaryDocRoles = db.GetCollection<ReviewAssignmentSummaryDocRole>(opt.ReviewAssignmentSummaryDocRoleCollection);
            await EnsureReviewAssignmentSummaryDocRolesAsync(reviewAssignmentSummaryDocRoles, ct);

            var docRoleProjectionRetryJobs = db.GetCollection<DocRoleReadModelProjectionRetryJob>(opt.DocRoleReadModelProjectionRetryJobCollection);
            await EnsureDocRoleProjectionRetryJobsAsync(docRoleProjectionRetryJobs, ct);

            // WORK HISTORIES
            var wh = db.GetCollection<WorkHistory>(opt.WorkHistoryCollection);
            await EnsureWorkHistoriesAsync(wh, ct);

            // EVALUATION TEMPLATES
            var evaluationTemplates = db.GetCollection<EvaluationTemplate>("evaluation_templates");
            await EnsureEvaluationTemplatesAsync(evaluationTemplates, ct);

            // COUNTERS
            var counters = db.GetCollection<CounterDoc>(opt.CounterCollection);
            await EnsureCountersAsync(counters, ct);

            // WORK ASSIGNMENTS
            var workAssignment = db.GetCollection<WorkAssignment>(opt.WorkAssignmentCollection);
            await EnsureWorkAssignmentsAsync(workAssignment, ct);

            var workAssignmentAggregateConfigs = db.GetCollection<WorkAssignmentAggregateConfig>(opt.WorkAssignmentAggregateConfigCollection);
            await EnsureWorkAssignmentAggregateConfigsAsync(workAssignmentAggregateConfigs, ct);

            var workAssignmentBasicSummaryConfigs = db.GetCollection<WorkAssignmentBasicSummaryConfig>(opt.WorkAssignmentBasicSummaryConfigCollection);
            await EnsureWorkAssignmentBasicSummaryConfigsAsync(workAssignmentBasicSummaryConfigs, ct);

            var workAssignmentBasicSummarySnapshots = db.GetCollection<WorkAssignmentBasicSummarySnapshot>(opt.WorkAssignmentBasicSummarySnapshotCollection);
            await EnsureWorkAssignmentBasicSummarySnapshotsAsync(workAssignmentBasicSummarySnapshots, ct);

            var workAssignmentAdvancedSummaryConfigs = db.GetCollection<WorkAssignmentAdvancedSummaryConfig>(opt.WorkAssignmentAdvancedSummaryConfigCollection);
            await EnsureWorkAssignmentAdvancedSummaryConfigsAsync(workAssignmentAdvancedSummaryConfigs, ct);

            var workAssignmentAdvancedSummaryDayNodes = db.GetCollection<WorkAssignmentAdvancedSummaryDayNode>(opt.WorkAssignmentAdvancedSummaryDayNodeCollection);
            await EnsureWorkAssignmentAdvancedSummaryHierarchyNodesAsync(
                workAssignmentAdvancedSummaryDayNodes,
                "Day",
                "dayKey",
                ct);

            var workAssignmentAdvancedSummaryMonthNodes = db.GetCollection<WorkAssignmentAdvancedSummaryMonthNode>(opt.WorkAssignmentAdvancedSummaryMonthNodeCollection);
            await EnsureWorkAssignmentAdvancedSummaryHierarchyNodesAsync(
                workAssignmentAdvancedSummaryMonthNodes,
                "Month",
                "monthKey",
                ct);

            var workAssignmentAdvancedSummaryYearNodes = db.GetCollection<WorkAssignmentAdvancedSummaryYearNode>(opt.WorkAssignmentAdvancedSummaryYearNodeCollection);
            await EnsureWorkAssignmentAdvancedSummaryHierarchyNodesAsync(
                workAssignmentAdvancedSummaryYearNodes,
                "Year",
                "yearKey",
                ct);

            var workSummaryTokenLedgers = db.GetCollection<WorkSummaryTokenLedger>(opt.WorkSummaryTokenLedgerCollection);
            await EnsureWorkSummaryTokenLedgersAsync(workSummaryTokenLedgers, ct);

            // P4 metadata must converge before the new uniqueness/search indexes
            // begin enforcing the family/version shape.
            await DynamicFlowDefinitionMetadataBackfill.RunAsync(db, opt, ct);

            var dynamicFlowTemplates = db.GetCollection<DynamicFlowTemplate>(opt.DynamicFlowTemplateCollection);
            await EnsureDynamicFlowTemplatesAsync(dynamicFlowTemplates, ct);

            var dynamicFlowTemplateVersions = db.GetCollection<DynamicFlowTemplateVersion>(opt.DynamicFlowTemplateVersionCollection);
            await EnsureDynamicFlowTemplateVersionsAsync(dynamicFlowTemplateVersions, ct);

            var dynamicFlowDefinitionCommandReceipts = db.GetCollection<DynamicFlowDefinitionCommandReceipt>(
                opt.DynamicFlowDefinitionCommandReceiptCollection);
            await EnsureDynamicFlowDefinitionCommandReceiptsAsync(dynamicFlowDefinitionCommandReceipts, ct);

            var dynamicFlowEvents = db.GetCollection<DynamicFlowEvent>(opt.DynamicFlowEventCollection);
            await EnsureDynamicFlowEventsAsync(dynamicFlowEvents, ct);

            await EnsureDynamicFlowRuntimePersistenceAsync(db, opt, ct);
            await EnsureDynamicFlowMappingPersistenceAsync(db, opt, ct);

            // WORK TEMPLATE ASSIGNEES
            var workTemplateAssignees = db.GetCollection<WorkTemplateAssignee>(opt.WorkTemplateAssigneeCollection);
            await EnsureWorkTemplateAssigneesAsync(workTemplateAssignees, ct);

            // WORK ASSIGNMENT REPORTS
            var workAssignmentReports = db.GetCollection<WorkAssignmentReport>(opt.WorkAssignmentReportCollection);
            await EnsureWorkAssignmentReportsAsync(workAssignmentReports, ct);

            var workAssignmentReportSections = db.GetCollection<WorkAssignmentReportSection>(opt.WorkAssignmentReportSectionCollection);
            await EnsureWorkAssignmentReportSectionsAsync(workAssignmentReportSections, ct);

            // WORK REPORT PAYLOADS
            var workReportPayloads = db.GetCollection<WorkReportPayload>(opt.WorkReportPayloadCollection);
            await EnsureWorkReportPayloadsAsync(workReportPayloads, ct);

            var workReportTableValues = db.GetCollection<WorkReportTableValue>(opt.WorkReportTableValueCollection);
            await EnsureWorkReportTableValuesAsync(workReportTableValues, ct);

            // WORK REPORT PERIODS
            var workReportPeriods = db.GetCollection<WorkReportPeriod>(opt.WorkReportPeriodCollection);
            await EnsureWorkReportPeriodsAsync(workReportPeriods, ct);

            // WORK ASSIGNMENT REPORT LOGS
            var workAssignmentReportLogs = db.GetCollection<WorkAssignmentReportLog>(opt.WorkAssignmentReportLogCollection);
            await EnsureWorkAssignmentReportLogsAsync(workAssignmentReportLogs, ct);

            // WORK ASSIGNMENT HANDOVER HISTORIES
            var workAssignmentHandoverHistories = db.GetCollection<WorkAssignmentHandoverHistory>(opt.WorkAssignmentHandoverHistoryCollection);
            await EnsureWorkAssignmentHandoverHistoriesAsync(workAssignmentHandoverHistories, ct);

            // WORK STATUS OPERATION LOGS
            var workStatusOperationLogs = db.GetCollection<WorkStatusOperationLog>(opt.WorkStatusOperationLogCollection);
            await EnsureWorkStatusOperationLogsAsync(workStatusOperationLogs, ct);

            // DYNAMIC FORM CLONE REQUESTS
            var dynamicFormCloneRequests = db.GetCollection<DynamicFormCloneRequest>(opt.DynamicFormCloneRequestCollection);
            await EnsureDynamicFormCloneRequestsAsync(dynamicFormCloneRequests, ct);

            // USER ACTION LOGS
            var userActionLogs = db.GetCollection<UserActionLog>(opt.UserActionLogCollection);
            await EnsureUserActionLogsAsync(userActionLogs, ct);

            var userActionLogRetryJobs = db.GetCollection<UserActionLogRetryJob>(opt.UserActionLogRetryJobCollection);
            await EnsureUserActionLogRetryJobsAsync(userActionLogRetryJobs, ct);

            // WORK ASSIGNMENT QUEUE
            var workAssignmentQueue = db.GetCollection<WorkAssignmentQueueItem>(opt.WorkAssignmentQueueCollection);
            await EnsureWorkAssignmentQueueAsync(workAssignmentQueue, ct);

            // WORK ASSIGMENT EVALUATION LOG
            var workAssignmentEvaluationLogs = db.GetCollection<WorkAssignmentEvaluationLog>(opt.WorkAssignmentEvaluationLogCollection);
            await EnsureWorkAssignmentEvaluationLogsAsync(workAssignmentEvaluationLogs, ct);

            // WORK ASSIGNMENT MATERIALIZE JOB
            var workAssignmentMaterializeJobs = db.GetCollection<WorkAssignmentMaterializeJobs>("work_assignment_materialize_jobs");
            await EnsureWorkAssignmentMaterializeJobsAsync(workAssignmentMaterializeJobs, ct);

            // WORK REPORT LABEL STATISTICS
            var labelStatValues = db.GetCollection<WorkReportLabelStatValue>(opt.WorkReportLabelStatValueCollection);
            await EnsureWorkReportLabelStatValuesAsync(labelStatValues, ct);

            var labelStatAggregates = db.GetCollection<WorkReportLabelStatAggregate>(opt.WorkReportLabelStatAggregateCollection);
            await EnsureWorkReportLabelStatAggregatesAsync(labelStatAggregates, ct);

            // WORK REPORT TABLE STATISTICS
            var tableStatValues = db.GetCollection<WorkReportTableStatValue>(opt.WorkReportTableStatValueCollection);
            await EnsureWorkReportTableStatValuesAsync(tableStatValues, ct);

            var tableStatAggregates = db.GetCollection<WorkReportTableStatAggregate>(opt.WorkReportTableStatAggregateCollection);
            await EnsureWorkReportTableStatAggregatesAsync(tableStatAggregates, ct);

            // WORK REPORT FIELD STATISTICS
            var fieldStatValues = db.GetCollection<WorkReportFieldStatValue>(opt.WorkReportFieldStatValueCollection);
            await EnsureWorkReportFieldStatValuesAsync(fieldStatValues, ct);

            var fieldStatAggregates = db.GetCollection<WorkReportFieldStatAggregate>(opt.WorkReportFieldStatAggregateCollection);
            await EnsureWorkReportFieldStatAggregatesAsync(fieldStatAggregates, ct);

            var statisticRebuildJobs = db.GetCollection<WorkReportStatisticRebuildJob>(opt.WorkReportStatisticRebuildJobCollection);
            await EnsureWorkReportStatisticRebuildJobsAsync(statisticRebuildJobs, ct);

            var statisticReconciliations = db.GetCollection<StatisticReconciliationRun>(
                opt.StatisticReconciliationRunCollection);
            await EnsureStatisticReconciliationsAsync(statisticReconciliations, ct);
            var statisticReconciliationObservations = db.GetCollection<StatisticReconciliationObservation>(
                opt.StatisticReconciliationObservationCollection);
            await EnsureStatisticReconciliationObservationsAsync(
                statisticReconciliationObservations,
                ct);
            var statisticReconciliationReviews = db.GetCollection<StatisticReconciliationReview>(
                opt.StatisticReconciliationReviewCollection);
            await EnsureStatisticReconciliationReviewsAsync(
                statisticReconciliationReviews,
                ct);

            var statisticDiffConfigs = db.GetCollection<WorkReportStatisticDiffConfig>(opt.WorkReportStatisticDiffConfigCollection);
            await EnsureWorkReportStatisticDiffConfigsAsync(statisticDiffConfigs, ct);

            var statisticDiffResults = db.GetCollection<WorkReportStatisticDiffResult>(
                "work_report_statistic_diff_results");
            await EnsureWorkReportStatisticDiffResultsAsync(statisticDiffResults, ct);

            var statisticExports = db.GetCollection<StatRunExportArtifact>(
                opt.WorkReportStatisticExportCollection);
            await EnsureStatRunExportsAsync(statisticExports, ct);

            var statisticDiffExports = db.GetCollection<StatRunExportArtifact>(
                opt.WorkReportStatisticDiffExportCollection);
            await EnsureStatRunExportsAsync(statisticDiffExports, ct);

            // NOTIFICATIONS
            var notifications = db.GetCollection<UserNotification>(opt.NotificationCollection);
            await EnsureNotificationsAsync(notifications, ct);

            // DYNAMIC FORM RUNTIME PROVENANCE
            // Run after collection indexes exist. Dynamic Form version metadata is
            // already backfilled by EnsureDynamicFormsAsync near the start.
            await DynamicFormRuntimeProvenanceBackfill.RunAsync(db, opt, ct);
        }


        private static async Task EnsureEvaluationTemplatesAsync(IMongoCollection<EvaluationTemplate> col, CancellationToken ct)
        {
            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldAsync(
                col,
                field: "representativeCode",
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_evaluation_templates_code_active",
                key: new BsonDocument("representativeCode", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_evaluation_templates_scope_active",
                key: new BsonDocument
                {
                    { "unitCodeScope", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureUsersAsync(IMongoCollection<AppUser> col, CancellationToken ct)
        {
            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldAsync(
                col,
                field: "username",
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_users_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_users_username_active",
                key: new BsonDocument("username", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_users_unitId_isDeleted",
                key: new BsonDocument
                {
                    { "unitId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureRefreshTokensAsync(IMongoCollection<RefreshTokenDoc> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_refresh_userId",
                key: new BsonDocument("userId", 1)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ttl_refresh_expiresAt",
                key: new BsonDocument("expiresAt", 1),
                expireAfterSeconds: 0
            ), ct);
        }

        private static async Task EnsureUnitsAsync(IMongoCollection<Unit> col, CancellationToken ct)
        {
            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldAsync(
                col,
                field: "code",
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_units_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_units_parentUnitId_isDeleted",
                key: new BsonDocument
                {
                    { "parentUnitId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_units_code_active",
                key: new BsonDocument("code", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_units_code_isDeleted",
                key: new BsonDocument
                {
                    { "code", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_units_primaryUnitTypeCode_isDeleted",
                key: new BsonDocument
                {
                    { "primaryUnitTypeCode", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureUnitTypesAsync(IMongoCollection<UnitType> col, CancellationToken ct)
        {
            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldAsync(
                col,
                field: "code",
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_unitTypes_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_unitTypes_code_active",
                key: new BsonDocument("code", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);
        }

        private static async Task EnsurePositionsAsync(IMongoCollection<Position> col, CancellationToken ct)
        {
            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldAsync(
                col,
                field: "code",
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_positions_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_positions_code_active",
                key: new BsonDocument("code", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_positions_unitTypeCodes_order_active",
                key: new BsonDocument
                {
                    { "unitTypeCodes", 1 },
                    { "order", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureUnitHistoriesAsync(IMongoCollection<UnitVersionHistory> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_unitHist_unitId_versionDesc",
                key: new BsonDocument
                {
                    { "unitId", 1 },
                    { "version", -1 }
                }
            ), ct);
        }

        private static async Task EnsureFilesAsync(IMongoCollection<FileDoc> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_files_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_files_owner_createdAt_desc_isDeleted",
                key: new BsonDocument
                {
                    { "createdByUserId", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldsAsync(
                col,
                fields: new[] { "bucket", "objectKey" },
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_files_bucket_objectKey_active",
                key: new BsonDocument
                {
                    { "bucket", 1 },
                    { "objectKey", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_files_upload_owner_isDeleted",
                key: new BsonDocument
                {
                    { "uploadId", 1 },
                    { "createdByUserId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_files_work_scope_createdAt_desc_isDeleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "documentScope", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_files_work_assignment_createdAt_desc_isDeleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "assignmentId", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureDynamicExcelAsync(IMongoCollection<DynamicExcelTemplate> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_dynamicExcel_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldAsync(
                col,
                field: "code",
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_dynamicExcel_code_active",
                key: new BsonDocument("code", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicExcel_createdAt_desc",
                key: new BsonDocument
                {
                    { "createdAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicExcel_createdBy_createdAt_desc",
                key: new BsonDocument
                {
                    { "createdByUserId", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicExcel_name_isDeleted",
                key: new BsonDocument
                {
                    { "name", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

        }

        private static async Task EnsureDynamicFormsAsync(IMongoCollection<DynamicFormTemplate> col, CancellationToken ct)
        {
            await BackfillDynamicFormVersionMetadataAsync(col, ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_dynamicForms_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldsAsync(
                col,
                fields: new[] { "code", "versionNo" },
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.DropIfExistsAsync(
                col,
                "ux_dynamicForms_code_active",
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_dynamicForms_code_version_active",
                key: new BsonDocument
                {
                    { "code", 1 },
                    { "versionNo", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_dynamicForms_family_version_active",
                key: new BsonDocument
                {
                    { "familyId", 1 },
                    { "versionNo", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "familyId", new BsonDocument("$type", "objectId") }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_dynamicForms_default_wrap_reuse",
                key: new BsonDocument("wrapReuseKey", 1),
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "isActive", true },
                    { "isPublished", false },
                    { "wrapReuseKey", new BsonDocument("$type", "string") }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicForms_createdAt_desc",
                key: new BsonDocument
                {
                    { "createdAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicForms_createdBy_createdAt_desc",
                key: new BsonDocument
                {
                    { "createdByUserId", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicForms_status_createdAt_desc",
                key: new BsonDocument
                {
                    { "isPublished", 1 },
                    { "isActive", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicForms_name_isDeleted",
                key: new BsonDocument
                {
                    { "name", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicForms_tagCodes_isDeleted",
                key: new BsonDocument
                {
                    { "tagCodes", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicForms_excelBlockTemplate_isDeleted",
                key: new BsonDocument
                {
                    { "excelBlockDynamicExcelTemplateId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task BackfillDynamicFormVersionMetadataAsync(
            IMongoCollection<DynamicFormTemplate> col,
            CancellationToken ct)
        {
            await DynamicFormVersionMetadataBackfill.RunAsync(col, ct);
        }

        private static async Task EnsureDynamicFormSectionsAsync(
            IMongoCollection<DynamicFormSectionDocument> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_dynamicFormSections_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_dynamicFormSections_template_section_active",
                key: new BsonDocument
                {
                    { "dynamicFormTemplateId", 1 },
                    { "sectionId", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicFormSections_template_order_active",
                key: new BsonDocument
                {
                    { "dynamicFormTemplateId", 1 },
                    { "order", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureLabelsAsync(IMongoCollection<LabelCatalogItem> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_labels_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_labels_scope_code_active",
                key: new BsonDocument
                {
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "code", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_labels_scope_name_active",
                key: new BsonDocument
                {
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "nameLower", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_labels_group_active",
                key: new BsonDocument
                {
                    { "groupCode", 1 },
                    { "usage", 1 },
                    { "dataType", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_labels_updatedAt_desc",
                key: new BsonDocument
                {
                    { "updatedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_labels_value_source_catalog_active",
                key: new BsonDocument
                {
                    { "valueSourceType", 1 },
                    { "valueSourceCatalogId", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureLabelEnumCatalogsAsync(IMongoCollection<LabelEnumCatalog> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_labelEnumCatalogs_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_labelEnumCatalogs_scope_code_active",
                key: new BsonDocument
                {
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "code", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_labelEnumCatalogs_scope_name_active",
                key: new BsonDocument
                {
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "nameLower", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_labelEnumCatalogs_unit_scope_active",
                key: new BsonDocument
                {
                    { "scopeType", 1 },
                    { "scopeUnitCode", 1 },
                    { "scopeLevel", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_labelEnumCatalogs_updatedAt_desc",
                key: new BsonDocument
                {
                    { "updatedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureLabelEnumOptionReadModelsAsync(IMongoCollection<LabelEnumOptionReadModel> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_labelEnumOptions_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_labelEnumOptions_catalog_code_active",
                key: new BsonDocument
                {
                    { "catalogId", 1 },
                    { "code", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_labelEnumOptions_catalog_search_active",
                key: new BsonDocument
                {
                    { "catalogId", 1 },
                    { "isActive", 1 },
                    { "searchText", 1 },
                    { "order", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_labelEnumOptions_scope_search_active",
                key: new BsonDocument
                {
                    { "scopeType", 1 },
                    { "scopeUnitCode", 1 },
                    { "scopeLevel", 1 },
                    { "isActive", 1 },
                    { "searchText", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorksAsync(IMongoCollection<Work> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_works_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldAsync(
                col,
                field: "autoCode",
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_works_autoCode_active",
                key: new BsonDocument("autoCode", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_works_name_isDeleted",
                key: new BsonDocument
                {
                    { "name", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_works_status_isDeleted",
                key: new BsonDocument
                {
                    { "status", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_works_type_createdAt_desc_isDeleted",
                key: new BsonDocument
                {
                    { "type", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_works_type_priority_createdAt_desc_isDeleted",
                key: new BsonDocument
                {
                    { "type", 1 },
                    { "priority", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_works_leaderDirective_isDeleted",
                key: new BsonDocument
                {
                    { "leaderDirectiveUserId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_works_dueDate_isDeleted",
                key: new BsonDocument
                {
                    { "dueDate", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureDocRolesAsync(IMongoCollection<DocRole> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_docRoles_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldsAsync(
                col,
                fields: new[] { "docType", "docId", "userId", "role" },
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_docRoles_docType_docId_userId_role_active",
                key: new BsonDocument
                {
                    { "docType", 1 },
                    { "docId", 1 },
                    { "userId", 1 },
                    { "role", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_docRoles_docType_userId_isDeleted",
                key: new BsonDocument
                {
                    { "docType", 1 },
                    { "userId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_docRoles_docType_docId_isDeleted",
                key: new BsonDocument
                {
                    { "docType", 1 },
                    { "docId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_docRoles_docType_docId_role_isDeleted",
                key: new BsonDocument
                {
                    { "docType", 1 },
                    { "docId", 1 },
                    { "role", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkListDocRolesAsync(IMongoCollection<WorkListDocRole> col, CancellationToken ct)
        {
            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldsAsync(
                col,
                fields: new[] { "userId", "docId" },
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workDocRoles_user_doc_active",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "docId", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workDocRoles_user_type_status_updated",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "type", 1 },
                    { "status", 1 },
                    { "isDeleted", 1 },
                    { "workCreatedAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workDocRoles_user_type_priority_created",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "type", 1 },
                    { "priority", 1 },
                    { "isDeleted", 1 },
                    { "workCreatedAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workDocRoles_user_type_leader_due",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "type", 1 },
                    { "leaderDirectiveUserId", 1 },
                    { "isDeleted", 1 },
                    { "dueDate", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workDocRoles_user_type_autoCode",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "type", 1 },
                    { "autoCode", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureAssignmentListDocRolesAsync(IMongoCollection<AssignmentListDocRole> col, CancellationToken ct)
        {
            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldsAsync(
                col,
                fields: new[] { "userId", "docId" },
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_assignmentDocRoles_user_doc_active",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "docId", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_assignmentDocRoles_user_work_active_path",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "workId", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 },
                    { "path", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_assignmentDocRoles_user_work_parent_active_updated",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "workId", 1 },
                    { "parentAssignmentId", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 },
                    { "assignmentUpdatedAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_assignmentDocRoles_user_work_visible_flow_path",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "workId", 1 },
                    { "visibleUnitIds", 1 },
                    { "flowInstanceId", 1 },
                    { "isDeleted", 1 },
                    { "path", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_assignmentDocRoles_user_work_progress_due",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "workId", 1 },
                    { "progressStatus", 1 },
                    { "hasOverduePeriod", -1 },
                    { "latestDueAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_assignmentDocRoles_user_work_template",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "workId", 1 },
                    { "dynamicExcelId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_assignmentDocRoles_user_work_form_template",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "workId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureMyReportTemplateListDocRolesAsync(IMongoCollection<MyReportTemplateListDocRole> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.DropIfExistsAsync(col, "ux_myReportTemplateListDocRoles_user_work_template_active", ct);

            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldsAsync(
                col,
                fields: new[] { "userId", "workId", "dynamicFormTemplateId" },
                matchFilter: new BsonDocument
                {
                    { "isDeleted", false },
                    { "dynamicFormTemplateId", new BsonDocument("$type", "objectId") }
                },
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_myReportTemplateListDocRoles_user_work_form_active",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "workId", 1 },
                    { "dynamicFormTemplateId", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "dynamicFormTemplateId", new BsonDocument("$type", "objectId") }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_myReportTemplateListDocRoles_user_work_overdue_updated",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "workId", 1 },
                    { "hasOverduePeriod", -1 },
                    { "isDeleted", 1 },
                    { "latestUpdatedAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_myReportTemplateListDocRoles_user_work_due",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "workId", 1 },
                    { "isDeleted", 1 },
                    { "latestDueAtUtc", -1 }
                }
            ), ct);
        }

        private static async Task EnsureMyReportPeriodListDocRolesAsync(IMongoCollection<MyReportPeriodListDocRole> col, CancellationToken ct)
        {
            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldsAsync(
                col,
                fields: new[] { "userId", "workReportPeriodId" },
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_myReportPeriodListDocRoles_user_period_active",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "workReportPeriodId", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_myReportPeriodListDocRoles_user_work_template_due",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "workId", 1 },
                    { "dynamicExcelId", 1 },
                    { "isDeleted", 1 },
                    { "dueAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_myReportPeriodListDocRoles_user_work_form_due",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "workId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "isDeleted", 1 },
                    { "dueAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_myReportPeriodListDocRoles_user_work_status_due",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "workId", 1 },
                    { "periodStatus", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 },
                    { "dueAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_myReportPeriodListDocRoles_user_assignment_period",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "assignmentId", 1 },
                    { "periodKey", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureReviewReportListDocRolesAsync(IMongoCollection<ReviewReportListDocRole> col, CancellationToken ct)
        {
            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldsAsync(
                col,
                fields: new[] { "reviewerUserId", "workReportPeriodId" },
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_reviewReportListDocRoles_reviewer_period_active",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "workReportPeriodId", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_reviewReportListDocRoles_reviewer_work_waiting_due",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "workId", 1 },
                    { "waitingReview", -1 },
                    { "isDeleted", 1 },
                    { "sortDueAtUtc", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_reviewReportListDocRoles_reviewer_work_bucket_due",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "workId", 1 },
                    { "reviewStatusBucket", 1 },
                    { "isDeleted", 1 },
                    { "sortDueAtUtc", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_reviewReportListDocRoles_reviewer_work_template_unit_due",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "workId", 1 },
                    { "dynamicExcelId", 1 },
                    { "assigneeUnitId", 1 },
                    { "isDeleted", 1 },
                    { "sortDueAtUtc", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_reviewReportListDocRoles_reviewer_assignment_period",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "assignmentId", 1 },
                    { "periodKey", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_reviewReportListDocRoles_reviewer_work_period_due",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "workId", 1 },
                    { "periodKey", 1 },
                    { "isDeleted", 1 },
                    { "sortDueAtUtc", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_reviewReportListDocRoles_reviewer_work_assignee_due",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "workId", 1 },
                    { "assigneeUserId", 1 },
                    { "isDeleted", 1 },
                    { "sortDueAtUtc", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_reviewReportListDocRoles_reviewer_work_reportStatus_due",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "workId", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 },
                    { "sortDueAtUtc", 1 }
                }
            ), ct);
        }

        private static async Task EnsureReviewAssignmentSummaryDocRolesAsync(
            IMongoCollection<ReviewAssignmentSummaryDocRole> col,
            CancellationToken ct)
        {
            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldsAsync(
                col,
                fields: new[] { "reviewerUserId", "workId", "assignmentId" },
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_reviewAssignmentSummaryDocRoles_reviewer_work_assignment_active",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "workId", 1 },
                    { "assignmentId", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_reviewAssignmentSummaryDocRoles_reviewer_work_due",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "workId", 1 },
                    { "sortHasOverduePeriod", -1 },
                    { "isDeleted", 1 },
                    { "sortLatestDueAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_reviewAssignmentSummaryDocRoles_reviewer_work_waiting_due",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "workId", 1 },
                    { "waitingReviewCount", -1 },
                    { "isDeleted", 1 },
                    { "sortLatestDueAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_reviewAssignmentSummaryDocRoles_reviewer_work_bucket_due",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "workId", 1 },
                    { "reviewStatusBuckets", 1 },
                    { "isDeleted", 1 },
                    { "sortLatestDueAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_reviewAssignmentSummaryDocRoles_reviewer_work_template_due",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "workId", 1 },
                    { "dynamicExcelId", 1 },
                    { "isDeleted", 1 },
                    { "sortLatestDueAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_reviewAssignmentSummaryDocRoles_reviewer_work_period_due",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "workId", 1 },
                    { "periodKeys", 1 },
                    { "isDeleted", 1 },
                    { "sortLatestDueAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_reviewAssignmentSummaryDocRoles_reviewer_work_assignee",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "workId", 1 },
                    { "assigneeUserIds", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_reviewAssignmentSummaryDocRoles_reviewer_work_unit",
                key: new BsonDocument
                {
                    { "reviewerUserId", 1 },
                    { "workId", 1 },
                    { "assigneeUnitIds", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureDocRoleProjectionRetryJobsAsync(
            IMongoCollection<DocRoleReadModelProjectionRetryJob> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_docRoleProjectionRetryJobs_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_docRoleProjectionRetryJobs_active_dedupe",
                key: new BsonDocument { { "dedupeKey", 1 } },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "isActive", true }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_docRoleProjectionRetryJobs_ready_scan",
                key: new BsonDocument
                {
                    { "isActive", 1 },
                    { "status", 1 },
                    { "nextRetryAtUtc", 1 },
                    { "createdAtUtc", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_docRoleProjectionRetryJobs_lease",
                key: new BsonDocument
                {
                    { "status", 1 },
                    { "leaseUntilUtc", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_docRoleProjectionRetryJobs_target",
                key: new BsonDocument
                {
                    { "action", 1 },
                    { "workId", 1 },
                    { "assignmentId", 1 },
                    { "workReportPeriodId", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkHistoriesAsync(IMongoCollection<WorkHistory> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workHist_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workHist_workId_at_desc",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "atUtc", -1 }
                }
            ), ct);
        }

        private static async Task EnsureCountersAsync(IMongoCollection<CounterDoc> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_counters_key",
                key: new BsonDocument("key", 1),
                unique: true
            ), ct);
        }

        private static async Task EnsureWorkAssignmentsAsync(IMongoCollection<WorkAssignment> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workAssignments_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_work_createdBy_isActive_updatedAt_desc_isDeleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "createdByUserId", 1 },
                    { "isActive", 1 },
                    { "updatedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_work_dynamicExcel_isDeleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "dynamicExcelId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_work_dynamicForm_isDeleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            var activeDynamicFormAssignmentFilter = new BsonDocument
            {
                { "isDeleted", false },
                { "isActive", true },
                { "dynamicFormTemplateId", new BsonDocument
                    {
                        { "$type", "objectId" }
                    }
                }
            };

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_sibling_dynamicForm_active",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "parentAssignmentId", 1 },
                    { "dynamicFormTemplateId", 1 }
                },
                partial: activeDynamicFormAssignmentFilter
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_parent_isDeleted",
                key: new BsonDocument
                {
                    { "parentAssignmentId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_mindmap_roots",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "isDeleted", 1 },
                    { "isActive", 1 },
                    { "parentAssignmentId", 1 },
                    { "level", 1 },
                    { "path", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_mindmap_children",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "parentAssignmentId", 1 },
                    { "isDeleted", 1 },
                    { "isActive", 1 },
                    { "updatedAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_root_path_isDeleted",
                key: new BsonDocument
                {
                    { "rootAssignmentId", 1 },
                    { "path", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_flow_instance_branch_deleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "flowInstanceId", 1 },
                    { "flowBranchId", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument("flowInstanceId", new BsonDocument("$type", "objectId"))
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_flow_parentBranch_deleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "parentFlowBranchId", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument("parentFlowBranchId", new BsonDocument("$type", "objectId"))
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_flow_step_status_deleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "flowStepId", 1 },
                    { "flowEffectiveStatus", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument("flowStepId", new BsonDocument("$type", "string"))
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_root_flowStatus_deleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "rootAssignmentId", 1 },
                    { "flowEffectiveStatus", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument("flowEffectiveStatus", new BsonDocument("$type", "string"))
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_assignmentType_isDeleted",
                key: new BsonDocument
                {
                    { "assignmentType", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_aggregationType_isDeleted",
                key: new BsonDocument
                {
                    { "aggregationType", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_assignees_userId_isDeleted",
                key: new BsonDocument
                {
                    { "assignees.userId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_updatedAt_desc_isDeleted",
                key: new BsonDocument
                {
                    { "updatedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_isActive_isDeleted",
                key: new BsonDocument
                {
                    { "isActive", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_due_scan",
                key: new BsonDocument
                {
                    { "isActive", 1 },
                    { "dueAtUtc", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_flowParticipant_createdBy_access",
                key: new BsonDocument
                {
                    { "createdByUserId", 1 },
                    { "isActive", 1 },
                    { "flowEffectiveStatus", 1 },
                    { "isDeleted", 1 },
                    { "flowTemplateId", 1 },
                    { "flowTemplateVersionNo", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_flowParticipant_watcher_access",
                key: new BsonDocument
                {
                    { "leaderWatcherUserIds", 1 },
                    { "isActive", 1 },
                    { "flowEffectiveStatus", 1 },
                    { "isDeleted", 1 },
                    { "flowTemplateId", 1 },
                    { "flowTemplateVersionNo", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_flowParticipant_assignee_access",
                key: new BsonDocument
                {
                    { "assignees.userId", 1 },
                    { "isActive", 1 },
                    { "flowEffectiveStatus", 1 },
                    { "isDeleted", 1 },
                    { "flowTemplateId", 1 },
                    { "flowTemplateVersionNo", 1 }
                }
            ), ct);

        }

        private static async Task EnsureWorkAssignmentAggregateConfigsAsync(
            IMongoCollection<WorkAssignmentAggregateConfig> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentAggregateConfigs_assignment_active",
                key: new BsonDocument
                {
                    { "assignmentId", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 },
                    { "updatedAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentAggregateConfigs_work_source_target",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "sourceDynamicFormTemplateId", 1 },
                    { "sourceBlockId", 1 },
                    { "targetDynamicFormTemplateId", 1 },
                    { "targetBlockId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkAssignmentBasicSummarySnapshotsAsync(
            IMongoCollection<WorkAssignmentBasicSummarySnapshot> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workAssignmentBasicSummarySnapshots_request_active",
                key: new BsonDocument("requestHash", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentBasicSummarySnapshots_scope",
                key: new BsonDocument
                {
                    { "scopeAssignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "snapshotDirty", 1 },
                    { "isDeleted", 1 },
                    { "updatedAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentBasicSummarySnapshots_dirty_scan",
                key: new BsonDocument
                {
                    { "snapshotDirty", 1 },
                    { "snapshotDirtyAtUtc", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentBasicSummarySnapshots_refresh_status",
                key: new BsonDocument
                {
                    { "refreshStatus", 1 },
                    { "refreshQueuedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentBasicSummarySnapshots_refresh_correlation",
                key: new BsonDocument
                {
                    { "refreshCorrelationId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentBasicSummarySnapshots_flow_scope",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "sourceFlowInstanceId", 1 },
                    { "sourceScopeMode", 1 },
                    { "sourceFlowEffectiveStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentBasicSummarySnapshots_source_dirty",
                key: new BsonDocument
                {
                    { "sourceReportIds", 1 },
                    { "snapshotDirty", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkAssignmentBasicSummaryConfigsAsync(
            IMongoCollection<WorkAssignmentBasicSummaryConfig> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workAssignmentBasicSummaryConfigs_assignment_template_active",
                key: new BsonDocument
                {
                    { "assignmentId", 1 },
                    { "dynamicFormTemplateId", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "isActive", true }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentBasicSummaryConfigs_work",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "assignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "isDeleted", 1 },
                    { "updatedAtUtc", -1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkAssignmentAdvancedSummaryConfigsAsync(
            IMongoCollection<WorkAssignmentAdvancedSummaryConfig> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workAssignmentAdvancedSummaryConfigs_draft_scope",
                key: new BsonDocument
                {
                    { "assignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "sectionId", 1 },
                    { "status", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "status", WorkAssignmentAdvancedSummaryConfigStatuses.Draft }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workAssignmentAdvancedSummaryConfigs_locked_version",
                key: new BsonDocument
                {
                    { "assignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "sectionId", 1 },
                    { "versionNo", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "status", WorkAssignmentAdvancedSummaryConfigStatuses.Locked }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentAdvancedSummaryConfigs_scope_status",
                key: new BsonDocument
                {
                    { "assignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "sectionId", 1 },
                    { "status", 1 },
                    { "updatedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentAdvancedSummaryConfigs_preview_status",
                key: new BsonDocument
                {
                    { "previewStatus", 1 },
                    { "previewRequestedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentAdvancedSummaryConfigs_preview_correlation",
                key: new BsonDocument
                {
                    { "previewCorrelationId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentAdvancedSummaryConfigs_flow_source",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "sourceFlowInstanceId", 1 },
                    { "sourceScopeMode", 1 },
                    { "status", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workAssignmentAdvancedSummaryConfigs_config_version",
                key: new BsonDocument
                {
                    { "configId", 1 },
                    { "versionNo", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "configId", new BsonDocument("$exists", true) }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workAssignmentAdvancedSummaryConfigs_config_active_status",
                key: new BsonDocument
                {
                    { "configId", 1 },
                    { "status", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "configId", new BsonDocument("$exists", true) },
                    {
                        "status",
                        new BsonDocument(
                            "$in",
                            new BsonArray
                            {
                                WorkAssignmentAdvancedSummaryConfigStatuses.Draft,
                                WorkAssignmentAdvancedSummaryConfigStatuses.Locked
                            })
                    }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentAdvancedSummaryConfigs_scope_version",
                key: new BsonDocument
                {
                    { "assignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "sectionId", 1 },
                    { "versionNo", -1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkAssignmentAdvancedSummaryHierarchyNodesAsync<T>(
            IMongoCollection<T> col,
            string collectionLabel,
            string grainKeyField,
            CancellationToken ct)
            where T : WorkAssignmentAdvancedSummaryHierarchyNodeBase
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: $"ux_workAssignmentAdvancedSummary{collectionLabel}Nodes_scope_grain",
                key: new BsonDocument
                {
                    { "assignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "sectionId", 1 },
                    { "configId", 1 },
                    { "configVersionId", 1 },
                    { "configHash", 1 },
                    { "timeAxis", 1 },
                    { grainKeyField, 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: $"ix_workAssignmentAdvancedSummary{collectionLabel}Nodes_scope_range",
                key: new BsonDocument
                {
                    { "assignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "sectionId", 1 },
                    { "configId", 1 },
                    { "configVersionId", 1 },
                    { "configHash", 1 },
                    { "timeAxis", 1 },
                    { grainKeyField, 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: $"ix_workAssignmentAdvancedSummary{collectionLabel}Nodes_status_dirty",
                key: new BsonDocument
                {
                    { "status", 1 },
                    { "isDirty", 1 },
                    { "updatedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: $"ix_workAssignmentAdvancedSummary{collectionLabel}Nodes_correlation",
                key: new BsonDocument
                {
                    { "buildCorrelationId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: $"ux_workAssignmentAdvancedSummary{collectionLabel}Nodes_buildReceipt",
                key: new BsonDocument { { "buildReceiptId", 1 } },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "buildReceiptId", new BsonDocument("$type", "string") }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: $"ix_workAssignmentAdvancedSummary{collectionLabel}Nodes_lease",
                key: new BsonDocument
                {
                    { "status", 1 },
                    { "leaseExpiresAtUtc", 1 },
                    { "fenceToken", 1 },
                    { "updatedAtUtc", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: $"ix_workAssignmentAdvancedSummary{collectionLabel}Nodes_source_dirty",
                key: new BsonDocument
                {
                    { "sourceReportIds", 1 },
                    { "isDirty", 1 },
                    { "status", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkSummaryTokenLedgersAsync(
            IMongoCollection<WorkSummaryTokenLedger> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workSummaryTokenLedgers_pool_identity",
                key: new BsonDocument
                {
                    { "recordKind", 1 },
                    { "ownerUnitId", 1 },
                    { "periodMonthKey", 1 },
                    { "tokenKind", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "recordKind", WorkSummaryTokenLedgerRecordKinds.Pool }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workSummaryTokenLedgers_entry_request_replay",
                key: new BsonDocument
                {
                    { "recordKind", 1 },
                    { "ownerUnitId", 1 },
                    { "tokenKind", 1 },
                    { "requestTokenId", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "recordKind", WorkSummaryTokenLedgerRecordKinds.Entry },
                    {
                        "requestTokenId",
                        new BsonDocument("$type", "string")
                    }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workSummaryTokenLedgers_entry_compensation",
                key: new BsonDocument
                {
                    { "recordKind", 1 },
                    { "compensatesLedgerId", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "recordKind", WorkSummaryTokenLedgerRecordKinds.Entry },
                    { "direction", WorkSummaryTokenDirections.Compensate },
                    {
                        "compensatesLedgerId",
                        new BsonDocument("$type", "objectId")
                    }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workSummaryTokenLedgers_ownerUnit_month_kind",
                key: new BsonDocument
                {
                    { "ownerUnitId", 1 },
                    { "periodMonthKey", 1 },
                    { "tokenKind", 1 },
                    { "direction", 1 },
                    { "outcome", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workSummaryTokenLedgers_scope_config",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "workAssignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "sectionId", 1 },
                    { "configId", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workSummaryTokenLedgers_actor_created",
                key: new BsonDocument
                {
                    { "actorUserId", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureDynamicFlowTemplatesAsync(
            IMongoCollection<DynamicFlowTemplate> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_dynamicFlowTemplates_code_active",
                key: new BsonDocument
                {
                    { "code", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicFlowTemplates_status_updated",
                key: new BsonDocument
                {
                    { "status", 1 },
                    { "updatedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicFlowTemplates_currentVersion",
                key: new BsonDocument
                {
                    { "currentVersionId", 1 },
                    { "currentVersionNo", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicFlowTemplates_rootForm_status_updated",
                key: new BsonDocument
                {
                    { "rootDynamicFormTemplateId", 1 },
                    { "status", 1 },
                    { "updatedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicFlowTemplates_owner_status_updated",
                key: new BsonDocument
                {
                    { "ownerUserId", 1 },
                    { "status", 1 },
                    { "isDeleted", 1 },
                    { "updatedAtUtc", -1 },
                    { "_id", -1 }
                }
            ), ct);
        }

        private static async Task EnsureDynamicFlowTemplateVersionsAsync(
            IMongoCollection<DynamicFlowTemplateVersion> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_dynamicFlowTemplateVersions_template_version_active",
                key: new BsonDocument
                {
                    { "templateId", 1 },
                    { "versionNo", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicFlowTemplateVersions_template_status_updated",
                key: new BsonDocument
                {
                    { "templateId", 1 },
                    { "status", 1 },
                    { "updatedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicFlowTemplateVersions_hash",
                key: new BsonDocument
                {
                    { "payloadHash", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicFlowTemplateVersions_rootForm_status",
                key: new BsonDocument
                {
                    { "rootDynamicFormTemplateId", 1 },
                    { "status", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_dynamicFlowTemplateVersions_one_draft_per_template_active",
                key: new BsonDocument("templateId", 1),
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "status", DynamicFlowTemplateVersionStatuses.Draft }
                }
            ), ct);
        }

        private static async Task EnsureDynamicFlowDefinitionCommandReceiptsAsync(
            IMongoCollection<DynamicFlowDefinitionCommandReceipt> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_dynamicFlowDefinitionCommandReceipts_actor_kind_command",
                key: new BsonDocument
                {
                    { "actorUserId", 1 },
                    { "commandKind", 1 },
                    { "commandId", 1 }
                },
                unique: true
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicFlowDefinitionCommandReceipts_family_created",
                key: new BsonDocument
                {
                    { "familyId", 1 },
                    { "createdAtUtc", -1 }
                }
            ), ct);
        }

        private static async Task EnsureDynamicFlowEventsAsync(
            IMongoCollection<DynamicFlowEvent> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicFlowEvents_work_flow_action_at",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "flowInstanceId", 1 },
                    { "action", 1 },
                    { "actionAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicFlowEvents_assignment_at",
                key: new BsonDocument
                {
                    { "assignmentId", 1 },
                    { "actionAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicFlowEvents_affected_assignment_at",
                key: new BsonDocument
                {
                    { "affectedAssignmentIds", 1 },
                    { "actionAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureDynamicFlowRuntimePersistenceAsync(
            IMongoDatabase db,
            MongoOptions opt,
            CancellationToken ct)
        {
            var instances = db.GetCollection<DynamicFlowInstance>(opt.DynamicFlowInstanceCollection);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(instances, new IndexSpec(
                name: "ux_dynamicFlowInstances_launch_identity",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "flowTemplateVersionId", 1 },
                    { "periodKey", 1 },
                    { "launchCommandId", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(instances, new IndexSpec(
                name: "ix_dynamicFlowInstances_issuer_updated",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "issuerUnitId", 1 },
                    { "updatedAtUtc", -1 },
                    { "_id", -1 }
                },
                partial: new BsonDocument("isDeleted", false)
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(instances, new IndexSpec(
                name: "ux_dynamicFlowInstances_periodic_occurrence",
                key: new BsonDocument
                {
                    { "periodicScheduleId", 1 },
                    { "periodKey", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "periodicScheduleId", new BsonDocument("$type", "objectId") }
                }
            ), ct);

            var executionEpochs = db.GetCollection<DynamicFlowExecutionEpoch>(
                opt.DynamicFlowExecutionEpochCollection);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                executionEpochs,
                new IndexSpec(
                    name: "ux_dynamicFlowExecutionEpochs_instance_epoch",
                    key: new BsonDocument
                    {
                        { "flowInstanceId", 1 },
                        { "executionEpoch", 1 }
                    },
                    unique: true,
                    partial: new BsonDocument("isDeleted", false)),
                ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                executionEpochs,
                new IndexSpec(
                    name: "ix_dynamicFlowExecutionEpochs_instance_opened",
                    key: new BsonDocument
                    {
                        { "flowInstanceId", 1 },
                        { "executionEpoch", -1 }
                    },
                    partial: new BsonDocument("isDeleted", false)),
                ct);

            var periodicSchedules = db.GetCollection<DynamicFlowPeriodicSchedule>(
                opt.DynamicFlowPeriodicScheduleCollection);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(periodicSchedules, new IndexSpec(
                name: "ux_dynamicFlowPeriodicSchedules_identity",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "flowTemplateVersionId", 1 },
                    { "scheduleKey", 1 },
                    { "policyVersion", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(periodicSchedules, new IndexSpec(
                name: "ix_dynamicFlowPeriodicSchedules_due_lease",
                key: new BsonDocument
                {
                    { "state", 1 },
                    { "nextDueAtUtc", 1 },
                    { "leaseUntilUtc", 1 },
                    { "_id", 1 }
                },
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            var periodicOccurrences = db.GetCollection<DynamicFlowPeriodicOccurrence>(
                opt.DynamicFlowPeriodicOccurrenceCollection);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(periodicOccurrences, new IndexSpec(
                name: "ux_dynamicFlowPeriodicOccurrences_schedule_period",
                key: new BsonDocument
                {
                    { "scheduleId", 1 },
                    { "periodKey", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(periodicOccurrences, new IndexSpec(
                name: "ix_dynamicFlowPeriodicOccurrences_schedule_observed",
                key: new BsonDocument
                {
                    { "scheduleId", 1 },
                    { "observedAtUtc", -1 },
                    { "_id", -1 }
                },
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            var steps = db.GetCollection<DynamicFlowStepInstance>(opt.DynamicFlowStepInstanceCollection);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(steps, new IndexSpec(
                name: "ux_dynamicFlowStepInstances_business_key",
                key: new BsonDocument
                {
                    { "flowInstanceId", 1 },
                    { "executionEpoch", 1 },
                    { "flowStepId", 1 },
                    { "targetUnitId", 1 },
                    { "branchId", 1 },
                    { "attemptNo", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(steps, new IndexSpec(
                name: "ux_dynamicFlowStepInstances_topology_identity",
                key: new BsonDocument
                {
                    { "flowInstanceId", 1 },
                    { "executionEpoch", 1 },
                    { "flowStepId", 1 },
                    { "branchId", 1 },
                    { "attemptNo", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(steps, new IndexSpec(
                name: "ix_dynamicFlowStepInstances_epoch_branch_order",
                key: new BsonDocument
                {
                    { "flowInstanceId", 1 },
                    { "executionEpoch", 1 },
                    { "branchId", 1 },
                    { "stepOrder", 1 },
                    { "attemptNo", 1 }
                },
                partial: new BsonDocument("isDeleted", false)
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(steps, new IndexSpec(
                name: "ix_dynamicFlowStepInstances_inbox",
                key: new BsonDocument
                {
                    { "participantUserIds", 1 },
                    { "state", 1 },
                    { "updatedAtUtc", -1 },
                    { "_id", -1 }
                },
                partial: new BsonDocument("isDeleted", false)
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(steps, new IndexSpec(
                name: "ux_dynamicFlowStepInstances_assignment",
                key: new BsonDocument("assignmentId", 1),
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "assignmentId", new BsonDocument("$type", "objectId") }
                }
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(steps, new IndexSpec(
                name: "ix_dynamicFlowStepInstances_instance_state",
                key: new BsonDocument
                {
                    { "flowInstanceId", 1 },
                    { "state", 1 }
                },
                partial: new BsonDocument("isDeleted", false)
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(steps, new IndexSpec(
                name: "ux_dynamicFlowStepInstances_supplemental_identity",
                key: new BsonDocument
                {
                    { "flowInstanceId", 1 },
                    { "executionEpoch", 1 },
                    { "supplementalStepId", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "isSupplemental", true },
                    { "supplementalStepId", new BsonDocument("$type", "string") }
                }
            ), ct);

            var participants = db.GetCollection<DynamicFlowParticipantSnapshot>(
                opt.DynamicFlowParticipantSnapshotCollection);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(participants, new IndexSpec(
                name: "ux_dynamicFlowParticipantSnapshots_instance",
                key: new BsonDocument("flowInstanceId", 1),
                unique: true
            ), ct);

            var receipts = db.GetCollection<DynamicFlowRuntimeCommandReceipt>(
                opt.DynamicFlowRuntimeCommandReceiptCollection);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(receipts, new IndexSpec(
                name: "ux_dynamicFlowRuntimeCommandReceipts_scope_command",
                key: new BsonDocument
                {
                    { "scopeKind", 1 },
                    { "scopeId", 1 },
                    { "commandType", 1 },
                    { "commandId", 1 }
                },
                unique: true
            ), ct);

            var events = db.GetCollection<DynamicFlowRuntimeEvent>(opt.DynamicFlowRuntimeEventCollection);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(events, new IndexSpec(
                name: "ux_dynamicFlowRuntimeEvents_instance_sequence",
                key: new BsonDocument
                {
                    { "flowInstanceId", 1 },
                    { "sequence", 1 }
                },
                unique: true
            ), ct);

            var outbox = db.GetCollection<DynamicFlowRuntimeOutboxItem>(opt.DynamicFlowRuntimeOutboxCollection);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(outbox, new IndexSpec(
                name: "ux_dynamicFlowRuntimeOutbox_dedupe",
                key: new BsonDocument("dedupeKey", 1),
                unique: true
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(outbox, new IndexSpec(
                name: "ix_dynamicFlowRuntimeOutbox_due_lease",
                key: new BsonDocument
                {
                    { "status", 1 },
                    { "nextAttemptAtUtc", 1 },
                    { "leaseUntilUtc", 1 },
                    { "_id", 1 }
                }
            ), ct);

            var gateways = db.GetCollection<DynamicFlowGatewayInstance>(
                opt.DynamicFlowGatewayInstanceCollection);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(gateways, new IndexSpec(
                name: "ux_dynamicFlowGatewayInstances_version",
                key: new BsonDocument
                {
                    { "flowInstanceId", 1 },
                    { "executionEpoch", 1 },
                    { "gatewayInstanceId", 1 },
                    { "gatewayVersion", 1 }
                },
                unique: true
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(gateways, new IndexSpec(
                name: "ix_dynamicFlowGatewayInstances_state",
                key: new BsonDocument
                {
                    { "flowInstanceId", 1 },
                    { "state", 1 },
                    { "gatewayVersion", 1 }
                }
            ), ct);

            var contributions = db.GetCollection<DynamicFlowGatewayContribution>(
                opt.DynamicFlowGatewayContributionCollection);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(contributions, new IndexSpec(
                name: "ux_dynamicFlowGatewayContributions_identity",
                key: new BsonDocument
                {
                    { "gatewayInstanceId", 1 },
                    { "gatewayVersion", 1 },
                    { "contributionId", 1 }
                },
                unique: true
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(contributions, new IndexSpec(
                name: "ux_dynamicFlowGatewayContributions_step",
                key: new BsonDocument
                {
                    { "flowInstanceId", 1 },
                    { "stepInstanceId", 1 },
                    { "gatewayVersion", 1 }
                },
                unique: true
            ), ct);
        }

        private static async Task EnsureDynamicFlowMappingPersistenceAsync(
            IMongoDatabase db,
            MongoOptions opt,
            CancellationToken ct)
        {
            var receipts = db.GetCollection<DynamicFlowMappingApplyReceipt>(
                opt.DynamicFlowMappingApplyReceiptCollection);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(receipts, new IndexSpec(
                name: "ux_dynamicFlowMappingApplyReceipts_target_command",
                key: new BsonDocument
                {
                    { "targetReportId", 1 },
                    { "commandId", 1 }
                },
                unique: true
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(receipts, new IndexSpec(
                name: "ix_dynamicFlowMappingApplyReceipts_target_created",
                key: new BsonDocument
                {
                    { "targetReportId", 1 },
                    { "createdAtUtc", -1 },
                    { "_id", -1 }
                }
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(receipts, new IndexSpec(
                name: "ix_dynamicFlowMappingApplyReceipts_runtime_state",
                key: new BsonDocument
                {
                    { "runtimePin.flowInstanceId", 1 },
                    { "runtimePin.executionEpoch", 1 },
                    { "state", 1 },
                    { "updatedAtUtc", 1 },
                    { "_id", 1 }
                }
            ), ct);

            var provenance = db.GetCollection<DynamicFlowMappingProvenanceRecord>(
                opt.DynamicFlowMappingProvenanceCollection);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(provenance, new IndexSpec(
                name: "ux_dynamicFlowMappingProvenance_receipt",
                key: new BsonDocument("receiptId", 1),
                unique: true
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(provenance, new IndexSpec(
                name: "ux_dynamicFlowMappingProvenance_target_payload_revision",
                key: new BsonDocument
                {
                    { "targetReportId", 1 },
                    { "targetPayloadRevision", 1 }
                },
                unique: true
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(provenance, new IndexSpec(
                name: "ix_dynamicFlowMappingProvenance_source_state",
                key: new BsonDocument
                {
                    { "sourcePins.sourceReportId", 1 },
                    { "state", 1 },
                    { "_id", 1 }
                }
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(provenance, new IndexSpec(
                name: "ix_dynamicFlowMappingProvenance_runtime_state",
                key: new BsonDocument
                {
                    { "runtimePin.flowInstanceId", 1 },
                    { "runtimePin.executionEpoch", 1 },
                    { "state", 1 },
                    { "targetReportId", 1 },
                    { "_id", 1 }
                }
            ), ct);

            var events = db.GetCollection<DynamicFlowMappingEvent>(
                opt.DynamicFlowMappingEventCollection);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(events, new IndexSpec(
                name: "ux_dynamicFlowMappingEvents_event_key",
                key: new BsonDocument("eventKey", 1),
                unique: true
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(events, new IndexSpec(
                name: "ux_dynamicFlowMappingEvents_receipt_type",
                key: new BsonDocument
                {
                    { "receiptId", 1 },
                    { "eventType", 1 }
                },
                unique: true
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(events, new IndexSpec(
                name: "ix_dynamicFlowMappingEvents_target_occurred",
                key: new BsonDocument
                {
                    { "targetReportId", 1 },
                    { "occurredAtUtc", -1 },
                    { "_id", -1 }
                }
            ), ct);

            var outbox = db.GetCollection<DynamicFlowMappingOutboxItem>(
                opt.DynamicFlowMappingOutboxCollection);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(outbox, new IndexSpec(
                name: "ux_dynamicFlowMappingOutbox_dedupe",
                key: new BsonDocument("dedupeKey", 1),
                unique: true
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(outbox, new IndexSpec(
                name: "ux_dynamicFlowMappingOutbox_receipt",
                key: new BsonDocument
                {
                    { "receiptId", 1 },
                    { "operation", 1 }
                },
                unique: true
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(outbox, new IndexSpec(
                name: "ix_dynamicFlowMappingOutbox_due_lease",
                key: new BsonDocument
                {
                    { "state", 1 },
                    { "nextAttemptAtUtc", 1 },
                    { "leaseUntilUtc", 1 },
                    { "_id", 1 }
                }
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(outbox, new IndexSpec(
                name: "ix_dynamicFlowMappingOutbox_target_state",
                key: new BsonDocument
                {
                    { "targetReportId", 1 },
                    { "state", 1 },
                    { "updatedAtUtc", -1 },
                    { "_id", -1 }
                }
            ), ct);
            await MongoIndexEnsureHelper.EnsureBySpecAsync(outbox, new IndexSpec(
                name: "ix_dynamicFlowMappingOutbox_runtime_state",
                key: new BsonDocument
                {
                    { "intent.runtimePin.flowInstanceId", 1 },
                    { "intent.runtimePin.executionEpoch", 1 },
                    { "state", 1 },
                    { "_id", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkTemplateAssigneesAsync(IMongoCollection<WorkTemplateAssignee> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workTemplateAssignees_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workTemplateAssignees_work_assignee_active_isDeleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "assigneeUserId", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workTemplateAssignees_work_template_assignee_active_isDeleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "dynamicExcelId", 1 },
                    { "assigneeUserId", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workTemplateAssignees_work_form_assignee_active_isDeleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "assigneeUserId", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workTemplateAssignees_assignment_isDeleted",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workTemplateAssignees_mindmap_template_users",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "dynamicExcelId", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 },
                    { "assigneeFullName", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workTemplateAssignees_mindmap_form_users",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 },
                    { "assigneeFullName", 1 }
                }
            ), ct);

            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldsAsync(
                col,
                fields: new[]
                {
                    "workId",
                    "dynamicFormTemplateId",
                    "assigneeUserId",
                    "dynamicFlowSupplementalStepId"
                },
                matchFilter: new BsonDocument
                {
                    { "isDeleted", false },
                    { "isActive", true },
                    { "dynamicFormTemplateId", new BsonDocument
                        {
                            { "$type", "objectId" }
                        }
                    }
                },
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workTemplateAssignees_work_template_assignee_active",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "assigneeUserId", 1 },
                    { "dynamicFlowSupplementalStepId", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "isActive", true },
                    { "dynamicFormTemplateId", new BsonDocument
                        {
                            { "$type", "objectId" }
                        }
                    }
                }
            ), ct);

            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldsAsync(
                col,
                fields: new[] { "workAssignmentId", "assigneeUserId" },
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workTemplateAssignees_assignment_assignee_active_doc",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "assigneeUserId", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);
        }

        private static async Task EnsureWorkAssignmentReportsAsync(
            IMongoCollection<WorkAssignmentReport> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workAssignmentReports_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workAssignmentReports_assignment_assignee_period_version_active",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "assigneeUserId", 1 },
                    { "periodInstanceKey", 1 },
                    { "versionNo", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "periodInstanceKey", new BsonDocument
                        {
                            { "$exists", true }
                        }
                    }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workAssignmentReports_assignment_assignee_period_current_active",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "assigneeUserId", 1 },
                    { "periodInstanceKey", 1 },
                    { "isCurrent", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "isCurrent", true },
                    { "periodInstanceKey", new BsonDocument
                        {
                            { "$exists", true }
                        }
                    }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_assignment_assignee_period_isDeleted_updatedAtUtc",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "assigneeUserId", 1 },
                    { "periodKey", 1 },
                    { "periodInstanceKey", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 },
                    { "updatedAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_template_current_active_id",
                key: new BsonDocument
                {
                    { "dynamicFormTemplateId", 1 },
                    { "isCurrent", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 },
                    { "_id", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_basicSummary_sources",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "status", 1 },
                    { "periodKey", 1 },
                    { "isCurrent", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 },
                    { "updatedAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_aggregate_refresh_candidates",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "dataOrigin", 1 },
                    { "aggregateSnapshotDirty", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 },
                    { "updatedAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_work_assignee_isDeleted_updatedAtUtc",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "assigneeUserId", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 },
                    { "updatedAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_status_isDeleted_updatedAtUtc",
                key: new BsonDocument
                {
                    { "status", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 },
                    { "updatedAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_template_isDeleted_updatedAtUtc",
                key: new BsonDocument
                {
                    { "dynamicExcelTemplateId", 1 },
                    { "isDeleted", 1 },
                    { "updatedAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_period_isDeleted_updatedAtUtc",
                key: new BsonDocument
                {
                    { "periodKey", 1 },
                    { "isDeleted", 1 },
                    { "updatedAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_dueAtUtc_status_isDeleted",
                key: new BsonDocument
                {
                    { "dueAtUtc", 1 },
                    { "status", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_lifecycleProjectionOutbox_pending",
                key: new BsonDocument
                {
                    { "lifecycleProjectionOutbox.state", 1 },
                    { "lifecycleProjectionClaimExpiresAtUtc", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            // Both outbox paths descend from the same array; every other key is scalar,
            // so this remains one legal multikey index rather than parallel arrays.
            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_lifecycle_direct_backfill",
                key: new BsonDocument
                {
                    { "status", 1 },
                    { "isCurrent", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 },
                    { "lifecycleProjectionOutbox.state", 1 },
                    { "lifecycleProjectionOutbox.toStatus", 1 },
                    { "lifecycleProjectionOutbox.toIsActive", 1 },
                    { "lifecycleProjectionOutbox.directProjectionState", 1 },
                    { "updatedAtUtc", 1 },
                    { "_id", 1 }
                }
            ), ct);

        }

        private static async Task EnsureWorkAssignmentReportSectionsAsync(
            IMongoCollection<WorkAssignmentReportSection> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workAssignmentReportSections_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workAssignmentReportSections_report_section_active",
                key: new BsonDocument
                {
                    { "workAssignmentReportId", 1 },
                    { "sectionId", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReportSections_report_order_active",
                key: new BsonDocument
                {
                    { "workAssignmentReportId", 1 },
                    { "sectionOrder", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReportSections_template_updated_active",
                key: new BsonDocument
                {
                    { "dynamicFormTemplateId", 1 },
                    { "sectionId", 1 },
                    { "hasData", 1 },
                    { "lastUpdatedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReportSections_report_lifecycle_active",
                key: new BsonDocument
                {
                    { "workAssignmentReportId", 1 },
                    { "sourcePayloadRevision", 1 },
                    { "sourceLifecycleRevision", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkReportPayloadsAsync(
            IMongoCollection<WorkReportPayload> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workReportPayloads_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workReportPayloads_report_active",
                key: new BsonDocument
                {
                    { "reportId", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportPayloads_report_revision",
                key: new BsonDocument
                {
                    { "reportId", 1 },
                    { "payloadRevision", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportPayloads_status_updated",
                key: new BsonDocument
                {
                    { "status", 1 },
                    { "updatedAtUtc", -1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkReportTableValuesAsync(
            IMongoCollection<WorkReportTableValue> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workReportTableValues_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workReportTableValues_report_block_active",
                key: new BsonDocument
                {
                    { "reportId", 1 },
                    { "blockId", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableValues_report_revision_order",
                key: new BsonDocument
                {
                    { "reportId", 1 },
                    { "payloadRevision", 1 },
                    { "blockOrder", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableValues_block_report",
                key: new BsonDocument
                {
                    { "blockId", 1 },
                    { "reportId", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkAssignmentEvaluationLogsAsync(
            IMongoCollection<WorkAssignmentEvaluationLog> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentEvaluationLogs_assignment_actionAt_desc_isDeleted",
                key: new BsonDocument
                {
            { "workAssignmentId", 1 },
            { "actionAtUtc", -1 },
            { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentEvaluationLogs_work_actionAt_desc_isDeleted",
                key: new BsonDocument
                {
            { "workId", 1 },
            { "actionAtUtc", -1 },
            { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentEvaluationLogs_actionBy_actionAt_desc_isDeleted",
                key: new BsonDocument
                {
            { "actionByUserId", 1 },
            { "actionAtUtc", -1 },
            { "isDeleted", 1 }
                }
            ), ct);
        }
        private static async Task EnsureWorkReportPeriodsAsync(IMongoCollection<WorkReportPeriod> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_work_report_period_runtime",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "assigneeUserId", 1 },
                    { "periodInstanceKey", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "periodInstanceKey", new BsonDocument
                        {
                            { "$exists", true }
                        }
                    }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_work_report_period_scheduled_lookup",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "assigneeUserId", 1 },
                    { "periodKey", 1 },
                    { "periodKind", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_work_report_period_assignment_status",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "status", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_work_report_period_mindmap_template_user",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "dynamicExcelId", 1 },
                    { "assigneeUserId", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 },
                    { "dueAtUtc", -1 },
                    { "periodKey", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_work_report_period_mindmap_form_user",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "assigneeUserId", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 },
                    { "dueAtUtc", -1 },
                    { "periodKey", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_work_report_period_due_scan",
                key: new BsonDocument
                {
                    { "isActive", 1 },
                    { "dueAtUtc", 1 },
                    { "status", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_work_report_period_lifecycle_source",
                key: new BsonDocument
                {
                    { "sourceLifecycleReportId", 1 },
                    { "sourceLifecycleRevision", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkAssignmentQueueAsync(IMongoCollection<WorkAssignmentQueueItem> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_work_assignment_queue_period",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "assigneeUserId", 1 },
                    { "periodKey", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_work_assignment_queue_scan",
                key: new BsonDocument
                {
                    { "isActive", 1 },
                    { "nextScanAtUtc", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkAssignmentReportLogsAsync(
            IMongoCollection<WorkAssignmentReportLog> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workAssignmentReportLogs_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workAssignmentReportLogs_lifecycle_event",
                key: new BsonDocument("lifecycleEventKey", 1),
                unique: true,
                partial: new BsonDocument(
                    "lifecycleEventKey",
                    new BsonDocument("$type", "string"))
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReportLogs_report_actionAt_desc_isDeleted",
                key: new BsonDocument
                {
                    { "workAssignmentReportId", 1 },
                    { "actionAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReportLogs_period_actionAt_desc_isDeleted",
                key: new BsonDocument
                {
                    { "workReportPeriodId", 1 },
                    { "actionAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReportLogs_work_assignment_actionAt_desc_isDeleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "workAssignmentId", 1 },
                    { "actionAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReportLogs_actionBy_actionAt_desc_isDeleted",
                key: new BsonDocument
                {
                    { "actionByUserId", 1 },
                    { "actionAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkStatusOperationLogsAsync(
            IMongoCollection<WorkStatusOperationLog> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workStatusOperationLogs_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workStatusOperationLogs_lifecycle_event",
                key: new BsonDocument("lifecycleEventKey", 1),
                unique: true,
                partial: new BsonDocument(
                    "lifecycleEventKey",
                    new BsonDocument("$type", "string"))
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workStatusOperationLogs_result_completed_desc_isDeleted",
                key: new BsonDocument
                {
                    { "result", 1 },
                    { "completedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workStatusOperationLogs_operation_completed_desc_isDeleted",
                key: new BsonDocument
                {
                    { "operation", 1 },
                    { "completedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workStatusOperationLogs_work_assignment_completed_desc_isDeleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "workAssignmentId", 1 },
                    { "completedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workStatusOperationLogs_period_report_completed_desc_isDeleted",
                key: new BsonDocument
                {
                    { "workReportPeriodId", 1 },
                    { "workAssignmentReportId", 1 },
                    { "completedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkAssignmentHandoverHistoriesAsync(
            IMongoCollection<WorkAssignmentHandoverHistory> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_assignmentHandoverHistories_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_assignmentHandoverHistories_work_created_desc",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_assignmentHandoverHistories_assignment_created_desc",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_assignmentHandoverHistories_operation",
                key: new BsonDocument
                {
                    { "operationId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureDynamicFormCloneRequestsAsync(
            IMongoCollection<DynamicFormCloneRequest> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_dynamicFormCloneRequests_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicFormCloneRequests_requester_created_desc",
                key: new BsonDocument
                {
                    { "requesterUserId", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicFormCloneRequests_owner_status_created_desc",
                key: new BsonDocument
                {
                    { "assignmentOwnerUserId", 1 },
                    { "status", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_dynamicFormCloneRequests_pending",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "requesterUserId", 1 },
                    { "status", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "status", DynamicFormCloneRequestStatus.Pending }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicFormCloneRequests_template_requester_status",
                key: new BsonDocument
                {
                    { "dynamicFormTemplateId", 1 },
                    { "requesterUserId", 1 },
                    { "status", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureUserActionLogsAsync(
            IMongoCollection<UserActionLog> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_userActionLogs_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_userActionLogs_idempotency_key",
                key: new BsonDocument("idempotencyKey", 1),
                unique: true,
                partial: new BsonDocument(
                    "idempotencyKey",
                    new BsonDocument("$type", "string"))
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_userActionLogs_dynamicFlow_receipt",
                key: new BsonDocument("dynamicFlowCommandReceiptId", 1),
                unique: true,
                partial: new BsonDocument(
                    "dynamicFlowCommandReceiptId",
                    new BsonDocument("$type", "objectId"))
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_userActionLogs_occurred_desc",
                key: new BsonDocument
                {
                    { "occurredAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_userActionLogs_action_occurred_desc",
                key: new BsonDocument
                {
                    { "action", 1 },
                    { "occurredAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_userActionLogs_unit_scope",
                key: new BsonDocument
                {
                    { "unitScopes.unitId", 1 },
                    { "occurredAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_userActionLogs_unit_level_scope",
                key: new BsonDocument
                {
                    { "unitScopes.unitLevel", 1 },
                    { "unitScopes.unitCode", 1 },
                    { "occurredAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_userActionLogs_user_scope",
                key: new BsonDocument
                {
                    { "userIds", 1 },
                    { "occurredAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_userActionLogs_work_assignment_period",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "workAssignmentId", 1 },
                    { "workReportPeriodId", 1 },
                    { "occurredAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureUserActionLogRetryJobsAsync(
            IMongoCollection<UserActionLogRetryJob> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_userActionLogRetryJobs_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_userActionLogRetryJobs_active_dedupe",
                key: new BsonDocument { { "dedupeKey", 1 } },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "isActive", true }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_userActionLogRetryJobs_ready_scan",
                key: new BsonDocument
                {
                    { "isActive", 1 },
                    { "status", 1 },
                    { "nextRetryAtUtc", 1 },
                    { "createdAtUtc", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_userActionLogRetryJobs_action_status",
                key: new BsonDocument
                {
                    { "action", 1 },
                    { "status", 1 },
                    { "isActive", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkAssignmentMaterializeJobsAsync(
            IMongoCollection<WorkAssignmentMaterializeJobs> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workAssignmentMaterializeJobs_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldAsync(
                col,
                field: "workAssignmentId",
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workAssignmentMaterializeJobs_assignment_active",
                key: new BsonDocument("workAssignmentId", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentMaterializeJobs_ready_scan",
                key: new BsonDocument
                {
            { "isActive", 1 },
            { "status", 1 },
            { "nextRetryAtUtc", 1 },
            { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentMaterializeJobs_lease",
                key: new BsonDocument
                {
            { "status", 1 },
            { "leaseUntilUtc", 1 },
            { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkReportStatisticRebuildJobsAsync(
            IMongoCollection<WorkReportStatisticRebuildJob> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workReportStatisticRebuildJobs_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workReportStatisticRebuildJobs_active_dedupe",
                key: new BsonDocument("dedupeKey", 1),
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "isActive", true }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workReportStatisticRebuildJobs_receipt",
                key: new BsonDocument("receiptId", 1),
                unique: true,
                partial: new BsonDocument(
                    "receiptId",
                    new BsonDocument("$type", "string"))
            ), ct);

            var legacySourceLifecycleEventIndex = new IndexSpec(
                name: "ux_workReportStatisticRebuildJobs_source_lifecycle_event",
                key: new BsonDocument
                {
                    { "sourceReportId", 1 },
                    { "sourceLifecycleEventKey", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "sourceReportId", new BsonDocument("$type", "objectId") },
                    { "sourceLifecycleEventKey", new BsonDocument("$type", "string") }
                });
            var versionedSourceLifecycleEventIndex = new IndexSpec(
                name: "ux_workReportStatisticRebuildJobs_source_lifecycle_event",
                key: new BsonDocument
                {
                    { "sourceReportId", 1 },
                    { "sourceLifecycleEventKey", 1 },
                    { "directProjectionIdentityKey", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "sourceReportId", new BsonDocument("$type", "objectId") },
                    { "sourceLifecycleEventKey", new BsonDocument("$type", "string") }
                });
            await MongoIndexEnsureHelper.MigrateExactOwnedIndexAsync(
                col,
                legacySourceLifecycleEventIndex,
                versionedSourceLifecycleEventIndex,
                ct);
            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(
                col,
                versionedSourceLifecycleEventIndex,
                ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_source_lifecycle_lookup",
                key: new BsonDocument
                {
                    { "sourceReportId", 1 },
                    { "sourceLifecycleRevision", 1 },
                    { "sourceLifecycleEventKey", 1 },
                    { "runKind", 1 },
                    { "status", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument(
                    "sourceLifecycleEventKey",
                    new BsonDocument("$type", "string"))
            ), ct);

            var legacyCurrentDirectPublicationIndex = new IndexSpec(
                name: "ux_workReportStatisticRebuildJobs_current_direct_publication",
                key: new BsonDocument
                {
                    { "runKind", 1 },
                    { "publicationScopeKey", 1 },
                    { "isCurrentPublication", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "runKind", WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection },
                    { "publicationScopeKey", new BsonDocument("$type", "string") },
                    { "isCurrentPublication", true }
                });
            var familyCurrentDirectPublicationIndex = new IndexSpec(
                name: "ux_workReportStatisticRebuildJobs_current_direct_publication",
                key: new BsonDocument
                {
                    { "runKind", 1 },
                    { "workId", 1 },
                    { "periodInstanceKey", 1 },
                    { "periodKind", 1 },
                    { "dynamicFormFamilyId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "dynamicFormVersionNo", 1 },
                    { "dynamicFormSchemaHash", 1 },
                    { "candidateChainId", 1 },
                    { "candidatePromptId", 1 },
                    { "isCurrentPublication", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "runKind", WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection },
                    { "isCurrentPublication", true }
                });
            await MongoIndexEnsureHelper.MigrateExactOwnedIndexAsync(
                col,
                legacyCurrentDirectPublicationIndex,
                familyCurrentDirectPublicationIndex,
                ct);
            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(
                col,
                familyCurrentDirectPublicationIndex,
                ct);
            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_current_direct_lookup",
                key: new BsonDocument
                {
                    { "publicationScopeKey", 1 },
                    { "isCurrentPublication", 1 },
                    { "status", 1 },
                    { "freshnessState", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument(
                    "runKind",
                    WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_receipt_lookup",
                key: new BsonDocument
                {
                    { "actorUserId", 1 },
                    { "tenantUnitId", 1 },
                    { "capabilityId", 1 },
                    { "commandId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_ready_scan",
                key: new BsonDocument
                {
                    { "isActive", 1 },
                    { "status", 1 },
                    { "nextRetryAtUtc", 1 },
                    { "priority", 1 },
                    { "createdAtUtc", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_p9_claim",
                key: new BsonDocument
                {
                    { "capabilityId", 1 },
                    { "isActive", 1 },
                    { "status", 1 },
                    { "nextRetryAtUtc", 1 },
                    { "leaseUntilUtc", 1 },
                    { "deadlineAtUtc", 1 },
                    { "priority", 1 },
                    { "createdAtUtc", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument(
                    "receiptId",
                    new BsonDocument("$type", "string"))
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_template_status",
                key: new BsonDocument
                {
                    { "dynamicFormTemplateId", 1 },
                    { "status", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_scope_status",
                key: new BsonDocument
                {
                    { "scopeKind", 1 },
                    { "workId", 1 },
                    { "workAssignmentId", 1 },
                    { "flowInstanceId", 1 },
                    { "periodInstanceKey", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "status", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_flow_contribution_source",
                key: new BsonDocument
                {
                    { "flowContributionSources.sourceReportId", 1 },
                    { "isCurrentPublication", 1 },
                    { "directPublicationRevision", -1 },
                    { "status", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument
                {
                    { "runKind", WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection },
                    { "flowContributionOperationVersion", "P9_FLOW_CONTRIBUTION_V1" }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_flow_contribution_provenance",
                key: new BsonDocument
                {
                    { "flowContributionSources.mappingProvenanceId", 1 },
                    { "isCurrentPublication", 1 },
                    { "publishedAtUtc", -1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument
                {
                    { "runKind", WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection },
                    { "flowContributionOperationVersion", "P9_FLOW_CONTRIBUTION_V1" }
                }
            ), ct);

            await MongoIndexEnsureHelper.MigrateExactOwnedIndexAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_contribution_source_v2",
                key: new BsonDocument
                {
                    { "flowContributionSources.sourceReportId", 1 },
                    { "nonFlowContributionSources.sourceReportId", 1 },
                    { "isCurrentPublication", 1 },
                    { "directPublicationRevision", -1 },
                    { "status", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument
                {
                    { "runKind", WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection },
                    { "flowContributionOperationVersion", "P9_CONTRIBUTION_V2" }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_flow_contribution_source_v2",
                key: new BsonDocument
                {
                    { "flowContributionSources.sourceReportId", 1 },
                    { "flowContributionOperationVersion", 1 },
                    { "isCurrentPublication", 1 },
                    { "directPublicationRevision", -1 },
                    { "status", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument
                {
                    { "runKind", WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection },
                    { "flowContributionOperationVersion", "P9_CONTRIBUTION_V2" }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_nonflow_contribution_source_v2",
                key: new BsonDocument
                {
                    { "nonFlowContributionSources.sourceReportId", 1 },
                    { "flowContributionOperationVersion", 1 },
                    { "isCurrentPublication", 1 },
                    { "directPublicationRevision", -1 },
                    { "status", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument
                {
                    { "runKind", WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection },
                    { "flowContributionOperationVersion", "P9_CONTRIBUTION_V2" }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_nonflow_contribution_policy_v2",
                key: new BsonDocument
                {
                    { "nonFlowContributionSources.policyOwnerId", 1 },
                    { "nonFlowContributionSources.policyRevision", 1 },
                    { "isCurrentPublication", 1 },
                    { "publishedAtUtc", -1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument
                {
                    { "runKind", WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection },
                    { "flowContributionOperationVersion", "P9_CONTRIBUTION_V2" }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_operations_scope",
                key: new BsonDocument
                {
                    { "candidateChainId", 1 },
                    { "actorUserId", 1 },
                    { "tenantUnitId", 1 },
                    { "workAssignmentId", 1 },
                    { "runKind", 1 },
                    { "status", 1 },
                    { "updatedAtUtc", -1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument(
                    "candidateChainId",
                    tdtd_be.Services.StatisticsRun.StatRunCapabilityActivation.RequiredChainId)
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_operations_cleanup",
                key: new BsonDocument
                {
                    { "candidateChainId", 1 },
                    { "status", 1 },
                    { "isCurrentPublication", 1 },
                    { "isActive", 1 },
                    { "updatedAtUtc", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument(
                    "candidateChainId",
                    tdtd_be.Services.StatisticsRun.StatRunCapabilityActivation.RequiredChainId)
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_operation_receipt",
                key: new BsonDocument
                {
                    { "operationReceipts.actorUserId", 1 },
                    { "operationReceipts.operation", 1 },
                    { "operationReceipts.commandId", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument(
                    "operationReceipts.receiptId",
                    new BsonDocument("$type", "string"))
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticRebuildJobs_reversal_source",
                key: new BsonDocument
                {
                    { "reversalAudit.priorRunId", 1 },
                    { "reversalAudit.sourceReportId", 1 },
                    { "status", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument(
                    "reversalAudit.auditHash",
                    new BsonDocument("$type", "string"))
            ), ct);

        }

        private static async Task EnsureStatisticReconciliationsAsync(
            IMongoCollection<StatisticReconciliationRun> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec(
                    "ix_statisticReconciliations_isDeleted",
                    new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_statisticReconciliations_receipt",
                key: new BsonDocument("receiptId", 1),
                unique: true,
                partial: new BsonDocument(
                    "receiptId",
                    new BsonDocument("$type", "string"))
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_statisticReconciliations_identity",
                key: new BsonDocument("immutableIdentityHash", 1),
                unique: true,
                partial: new BsonDocument(
                    "immutableIdentityHash",
                    new BsonDocument("$type", "string"))
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_statisticReconciliations_scope_list",
                key: new BsonDocument
                {
                    { "candidateChainId", 1 },
                    { "workId", 1 },
                    { "scopeAssignmentId", 1 },
                    { "createdAtUtc", -1 },
                    { "_id", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_statisticReconciliations_p9_result",
                key: new BsonDocument
                {
                    { "p9ResultKind", 1 },
                    { "p9ResultId", 1 },
                    { "p9RunId", 1 },
                    { "p9GenerationId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_statisticReconciliations_claim",
                key: new BsonDocument
                {
                    { "candidateChainId", 1 },
                    { "status", 1 },
                    { "pendingGenerationId", 1 },
                    { "nextRetryAtUtc", 1 },
                    { "leaseUntilUtc", 1 },
                    { "deadlineAtUtc", 1 },
                    { "createdAtUtc", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_statisticReconciliations_cleanup",
                key: new BsonDocument
                {
                    { "candidateChainId", 1 },
                    { "status", 1 },
                    { "updatedAtUtc", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_statisticReconciliations_operation_receipt",
                key: new BsonDocument
                {
                    { "operationReceipts.actorUserId", 1 },
                    { "operationReceipts.operation", 1 },
                    { "operationReceipts.commandId", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument(
                    "operationReceipts.receiptId",
                    new BsonDocument("$type", "string"))
            ), ct);
        }

        private static async Task EnsureStatisticReconciliationObservationsAsync(
            IMongoCollection<StatisticReconciliationObservation> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_statisticReconciliationObservations_atom",
                key: new BsonDocument
                {
                    { "reconciliationId", 1 },
                    { "generationId", 1 },
                    { "recordKind", 1 },
                    { "atom.identitySha256", 1 },
                    { "atom.atomKind", 1 },
                    { "atom.valueIdentitySha256", 1 }
                },
                unique: true,
                partial: new BsonDocument(
                    "recordKind",
                    StatisticReconciliationObservationRecordKinds.ExpectedAtom)
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_statisticReconciliationObservations_actual_atom",
                key: new BsonDocument
                {
                    { "reconciliationId", 1 },
                    { "generationId", 1 },
                    { "recordKind", 1 },
                    { "lineagePin.layer", 1 },
                    { "lineagePin.revision", 1 },
                    { "lineagePin.ownerId", 1 },
                    { "lineagePin.versionId", 1 },
                    { "lineagePin.sha256", 1 },
                    { "atom.identitySha256", 1 },
                    { "atom.atomKind", 1 },
                    { "atom.valueIdentitySha256", 1 }
                },
                unique: true,
                partial: new BsonDocument(
                    "recordKind",
                    StatisticReconciliationObservationRecordKinds.ActualAtom)
            ), ct);
            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_statisticReconciliationObservations_commit",
                key: new BsonDocument
                {
                    { "reconciliationId", 1 },
                    { "generationId", 1 },
                    { "recordKind", 1 }
                },
                unique: true,
                partial: new BsonDocument(
                    "recordKind",
                    StatisticReconciliationObservationRecordKinds.GenerationCommit)
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_statisticReconciliationObservations_generation_integrity",
                key: new BsonDocument
                {
                    { "reconciliationId", 1 },
                    { "generationId", 1 },
                    { "recordKind", 1 },
                    { "documentSemanticSha256", 1 }
                }
            ), ct);
            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_statisticReconciliationObservations_manifest_scan",
                key: new BsonDocument
                {
                    { "reconciliationId", 1 },
                    { "generationId", 1 },
                    { "_id", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_statisticReconciliationObservations_provenance_page",
                key: new BsonDocument
                {
                    { "reconciliationId", 1 },
                    { "generationId", 1 },
                    { "recordKind", 1 },
                    { "_id", 1 }
                }
            ), ct);
        }
        private static async Task EnsureStatisticReconciliationReviewsAsync(
            IMongoCollection<StatisticReconciliationReview> col,
            CancellationToken ct)
        {
            var legacyGenerationIndex = new IndexSpec(
                name: "ux_statisticReconciliationReviews_generation",
                key: new BsonDocument
                {
                    { "reconciliationId", 1 },
                    { "verdictGenerationId", 1 }
                },
                unique: true
            );
            var generationIndex = new IndexSpec(
                name: "ux_statisticReconciliationReviews_generation",
                key: new BsonDocument
                {
                    { "reconciliationId", 1 },
                    { "verdictGenerationId", 1 }
                },
                unique: true,
                partial: new BsonDocument(
                    "recordKind",
                    StatisticReconciliationReviewKinds.FinalVerdict)
            );
            await MongoIndexEnsureHelper.MigrateExactOwnedIndexAsync(
                col,
                legacyGenerationIndex,
                generationIndex,
                ct);
            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(
                col,
                generationIndex,
                ct);
            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_statisticReconciliationReviews_actualGeneration",
                key: new BsonDocument
                {
                    { "reconciliationId", 1 },
                    { "actualGenerationId", 1 },
                    { "actualGenerationSha256", 1 }
                },
                unique: true,
                partial: new BsonDocument(
                    "recordKind",
                    StatisticReconciliationReviewKinds.FinalVerdict)
            ), ct);
            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_statisticReconciliationReviews_supersedes",
                key: new BsonDocument
                {
                    { "reconciliationId", 1 },
                    { "supersedesVerdictGenerationId", 1 }
                },
                unique: true,
                partial: new BsonDocument(
                    "supersedesVerdictGenerationId",
                    new BsonDocument("$type", "string"))
            ), ct);
            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_statisticReconciliationReviews_lineage",
                key: new BsonDocument
                {
                    { "reconciliationId", 1 },
                    { "createdAtUtc", 1 },
                    { "verdictGenerationId", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkReportStatisticDiffConfigsAsync(
            IMongoCollection<WorkReportStatisticDiffConfig> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workReportStatisticDiffConfigs_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticDiffConfigs_work_assignment_template",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "assignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "isActive", 1 },
                    { "updatedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkReportStatisticDiffResultsAsync(
            IMongoCollection<WorkReportStatisticDiffResult> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_workReportStatisticDiffResults_command_active",
                key: new BsonDocument
                {
                    { "assignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "commandId", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_workReportStatisticDiffResults_run_active",
                key: new BsonDocument { { "runId", 1 } },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticDiffResults_scope_config_current",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "assignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "configId", 1 },
                    { "configVersionId", 1 },
                    { "configHash", 1 },
                    { "isCurrent", -1 },
                    { "completedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticDiffResults_queue_lease",
                key: new BsonDocument
                {
                    { "status", 1 },
                    { "leaseExpiresAtUtc", 1 },
                    { "attemptNo", 1 },
                    { "fenceToken", 1 },
                    { "updatedAtUtc", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticDiffResults_source_reverse",
                key: new BsonDocument
                {
                    { "sourcePins.sourceReportId", 1 },
                    { "sourcePins.sourceLifecycleRevision", 1 },
                    { "isCurrent", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportStatisticDiffResults_terminal_ttl",
                key: new BsonDocument { { "expiresAtUtc", 1 } },
                expireAfterSeconds: 0,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "isCurrent", false },
                    { "expiresAtUtc", new BsonDocument("$type", "date") }
                }
            ), ct);
        }

        private static async Task EnsureStatRunExportsAsync(
            IMongoCollection<StatRunExportArtifact> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_statRunExports_actor_command_active",
                key: new BsonDocument
                {
                    { "requestedByUserId", 1 },
                    { "commandId", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_statRunExports_scope_acl",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "capabilityId", 1 },
                    { "requestedByUserId", 1 },
                    { "expiresAtUtc", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_statRunExports_result_pins",
                key: new BsonDocument
                {
                    { "resultId", 1 },
                    { "resultHash", 1 },
                    { "configHash", 1 },
                    { "sourceHash", 1 },
                    { "format", 1 },
                    { "filterHash", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_statRunExports_expiry_cleanup",
                key: new BsonDocument
                {
                    { "expiresAtUtc", 1 },
                    { "status", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkReportLabelStatValuesAsync(
            IMongoCollection<WorkReportLabelStatValue> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workReportLabelStatValues_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_workReportLabelStatValues_report_block_row_label_active_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workAssignmentReportId", 1 },
                    { "directProjection.sourcePayloadRevision", 1 },
                    { "directProjection.sourceLifecycleRevision", 1 },
                    { "periodInstanceKey", 1 },
                    { "blockId", 1 },
                    { "sheetId", 1 },
                    { "rowKey", 1 },
                    { "labelCode", 1 },
                    { "source", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "directProjection.generationId", new BsonDocument("$type", "string") }
                }
            ), ct);

            // BSON null equality includes missing fields; generation rows always pin a string id.
            await MongoIndexEnsureHelper.MigrateExactOwnedIndexAsync(col, new IndexSpec(
                name: "ux_workReportLabelStatValues_report_block_row_label_active",
                key: new BsonDocument
                {
                    { "workAssignmentReportId", 1 },
                    { "blockId", 1 },
                    { "rowKey", 1 },
                    { "labelCode", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_workReportLabelStatValues_report_block_row_label_active_legacy",
                key: new BsonDocument
                {
                    { "workAssignmentReportId", 1 },
                    { "blockId", 1 },
                    { "rowKey", 1 },
                    { "labelCode", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "directProjection.generationId", BsonNull.Value }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportLabelStatValues_work_period_label_status_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "periodInstanceKey", 1 },
                    { "labelCode", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportLabelStatValues_assignment_label_period_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workAssignmentId", 1 },
                    { "labelCode", 1 },
                    { "periodInstanceKey", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportLabelStatValues_work_window_label_status_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "periodStartDate", 1 },
                    { "periodEndDate", 1 },
                    { "labelCode", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportLabelStatValues_unit_period_status_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "assigneeUnitId", 1 },
                    { "assignmentIsActive", 1 },
                    { "reportIsActive", 1 },
                    { "periodInstanceKey", 1 },
                    { "labelCode", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportLabelStatValues_flow_period_label_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "flowInstanceId", 1 },
                    { "flowEffectiveStatus", 1 },
                    { "periodInstanceKey", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "labelCode", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument("flowInstanceId", new BsonDocument("$type", "objectId"))
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportLabelStatValues_payload_freshness_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workAssignmentReportId", 1 },
                    { "sourcePayloadRevision", 1 },
                    { "sourcePayloadHash", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

        }

        private static async Task EnsureWorkReportLabelStatAggregatesAsync(
            IMongoCollection<WorkReportLabelStatAggregate> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workReportLabelStatAggregates_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_workReportLabelStatAggregates_scope_label_period_status_active_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "directProjection.configVersionId", 1 },
                    { "directProjection.configRevision", 1 },
                    { "directProjection.configHash", 1 },
                    { "directProjection.sourceMembershipSignature", 1 },
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "dynamicExcelTemplateId", 1 },
                    { "blockId", 1 },
                    { "labelCode", 1 },
                    { "periodInstanceKey", 1 },
                    { "reportStatus", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "directProjection.generationId", new BsonDocument("$type", "string") }
                }
            ), ct);

            await MongoIndexEnsureHelper.MigrateExactOwnedIndexAsync(col, new IndexSpec(
                name: "ux_workReportLabelStatAggregates_scope_label_period_status_active",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "dynamicExcelTemplateId", 1 },
                    { "blockId", 1 },
                    { "labelCode", 1 },
                    { "periodInstanceKey", 1 },
                    { "reportStatus", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_workReportLabelStatAggregates_scope_label_period_status_active_legacy",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "dynamicExcelTemplateId", 1 },
                    { "blockId", 1 },
                    { "labelCode", 1 },
                    { "periodInstanceKey", 1 },
                    { "reportStatus", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "directProjection.generationId", BsonNull.Value }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportLabelStatAggregates_tree_read_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "periodInstanceKey", 1 },
                    { "reportStatus", 1 },
                    { "rowCount", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportLabelStatAggregates_label_read_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "labelCode", 1 },
                    { "periodInstanceKey", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportLabelStatAggregates_window_read_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "periodStartDate", 1 },
                    { "periodEndDate", 1 },
                    { "labelCode", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkReportTableStatValuesAsync(
            IMongoCollection<WorkReportTableStatValue> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workReportTableStatValues_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_workReportTableStatValues_report_metric_source_active_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workAssignmentReportId", 1 },
                    { "directProjection.sourcePayloadRevision", 1 },
                    { "directProjection.sourceLifecycleRevision", 1 },
                    { "periodInstanceKey", 1 },
                    { "blockId", 1 },
                    { "metricKey", 1 },
                    { "sourceKey", 1 },
                    { "bucketKey", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "directProjection.generationId", new BsonDocument("$type", "string") }
                }
            ), ct);

            await MongoIndexEnsureHelper.MigrateExactOwnedIndexAsync(col, new IndexSpec(
                name: "ux_workReportTableStatValues_report_metric_source_active",
                key: new BsonDocument
                {
                    { "workAssignmentReportId", 1 },
                    { "blockId", 1 },
                    { "metricKey", 1 },
                    { "sourceKey", 1 },
                    { "bucketKey", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_workReportTableStatValues_report_metric_source_active_legacy",
                key: new BsonDocument
                {
                    { "workAssignmentReportId", 1 },
                    { "blockId", 1 },
                    { "metricKey", 1 },
                    { "sourceKey", 1 },
                    { "bucketKey", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "directProjection.generationId", BsonNull.Value }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableStatValues_work_period_metric_status_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "periodInstanceKey", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "blockId", 1 },
                    { "metricKey", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableStatValues_work_period_metric_label_status_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "periodInstanceKey", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "metricLabelCode", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableStatValues_assignment_metric_period_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workAssignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "blockId", 1 },
                    { "metricKey", 1 },
                    { "periodInstanceKey", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableStatValues_work_window_metric_status_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "periodStartDate", 1 },
                    { "periodEndDate", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "blockId", 1 },
                    { "metricKey", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableStatValues_unit_period_status_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "assigneeUnitId", 1 },
                    { "assignmentIsActive", 1 },
                    { "reportIsActive", 1 },
                    { "periodInstanceKey", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "blockId", 1 },
                    { "metricKey", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableStatValues_flow_period_metric_concept_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "flowInstanceId", 1 },
                    { "flowEffectiveStatus", 1 },
                    { "periodInstanceKey", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "blockId", 1 },
                    { "metricKey", 1 },
                    { "conceptCode", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument("flowInstanceId", new BsonDocument("$type", "objectId"))
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableStatValues_payload_freshness_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workAssignmentReportId", 1 },
                    { "sourcePayloadRevision", 1 },
                    { "sourcePayloadHash", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkReportTableStatAggregatesAsync(
            IMongoCollection<WorkReportTableStatAggregate> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workReportTableStatAggregates_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_workReportTableStatAggregates_scope_metric_period_status_active_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "directProjection.configVersionId", 1 },
                    { "directProjection.configRevision", 1 },
                    { "directProjection.configHash", 1 },
                    { "directProjection.sourceMembershipSignature", 1 },
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "dynamicExcelTemplateId", 1 },
                    { "blockId", 1 },
                    { "tableMode", 1 },
                    { "metricKey", 1 },
                    { "dataType", 1 },
                    { "bucketKey", 1 },
                    { "periodInstanceKey", 1 },
                    { "reportStatus", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "directProjection.generationId", new BsonDocument("$type", "string") }
                }
            ), ct);

            await MongoIndexEnsureHelper.MigrateExactOwnedIndexAsync(col, new IndexSpec(
                name: "ux_workReportTableStatAggregates_scope_metric_period_status_active",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "dynamicExcelTemplateId", 1 },
                    { "blockId", 1 },
                    { "tableMode", 1 },
                    { "metricKey", 1 },
                    { "dataType", 1 },
                    { "bucketKey", 1 },
                    { "periodInstanceKey", 1 },
                    { "reportStatus", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_workReportTableStatAggregates_scope_metric_period_status_active_legacy",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "dynamicExcelTemplateId", 1 },
                    { "blockId", 1 },
                    { "tableMode", 1 },
                    { "metricKey", 1 },
                    { "dataType", 1 },
                    { "bucketKey", 1 },
                    { "periodInstanceKey", 1 },
                    { "reportStatus", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "directProjection.generationId", BsonNull.Value }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableStatAggregates_tree_read_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "periodInstanceKey", 1 },
                    { "reportStatus", 1 },
                    { "sum", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableStatAggregates_metric_read_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "blockId", 1 },
                    { "metricKey", 1 },
                    { "periodInstanceKey", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableStatAggregates_metric_label_read_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "metricLabelCode", 1 },
                    { "periodInstanceKey", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableStatAggregates_window_read_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "blockId", 1 },
                    { "metricKey", 1 },
                    { "periodStartDate", 1 },
                    { "periodEndDate", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkReportFieldStatValuesAsync(
            IMongoCollection<WorkReportFieldStatValue> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workReportFieldStatValues_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_workReportFieldStatValues_report_field_source_active_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workAssignmentReportId", 1 },
                    { "directProjection.sourcePayloadRevision", 1 },
                    { "directProjection.sourceLifecycleRevision", 1 },
                    { "periodInstanceKey", 1 },
                    { "fieldId", 1 },
                    { "sourceKey", 1 },
                    { "bucketKey", 1 },
                    { "valueKind", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "directProjection.generationId", new BsonDocument("$type", "string") }
                }
            ), ct);

            await MongoIndexEnsureHelper.MigrateExactOwnedIndexAsync(col, new IndexSpec(
                name: "ux_workReportFieldStatValues_report_field_source_active",
                key: new BsonDocument
                {
                    { "workAssignmentReportId", 1 },
                    { "fieldId", 1 },
                    { "sourceKey", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_workReportFieldStatValues_report_field_source_active_legacy",
                key: new BsonDocument
                {
                    { "workAssignmentReportId", 1 },
                    { "fieldId", 1 },
                    { "sourceKey", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "directProjection.generationId", BsonNull.Value }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportFieldStatValues_work_period_field_status_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "periodInstanceKey", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "fieldId", 1 },
                    { "bucketKey", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportFieldStatValues_assignment_field_period_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workAssignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "fieldId", 1 },
                    { "periodInstanceKey", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportFieldStatValues_work_window_field_status_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "periodStartDate", 1 },
                    { "periodEndDate", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "fieldId", 1 },
                    { "bucketKey", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportFieldStatValues_unit_period_status_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "assigneeUnitId", 1 },
                    { "assignmentIsActive", 1 },
                    { "reportIsActive", 1 },
                    { "periodInstanceKey", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "fieldId", 1 },
                    { "bucketKey", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportFieldStatValues_flow_period_field_concept_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "flowInstanceId", 1 },
                    { "flowEffectiveStatus", 1 },
                    { "periodInstanceKey", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "fieldId", 1 },
                    { "conceptCode", 1 },
                    { "bucketKey", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                },
                partial: new BsonDocument("flowInstanceId", new BsonDocument("$type", "objectId"))
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportFieldStatValues_form_label_period_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "statisticLabelCodes", 1 },
                    { "periodKey", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportFieldStatValues_payload_freshness_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workAssignmentReportId", 1 },
                    { "sourcePayloadRevision", 1 },
                    { "sourcePayloadHash", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureWorkReportFieldStatAggregatesAsync(
            IMongoCollection<WorkReportFieldStatAggregate> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workReportFieldStatAggregates_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_workReportFieldStatAggregates_scope_field_bucket_period_status_active_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "directProjection.configVersionId", 1 },
                    { "directProjection.configRevision", 1 },
                    { "directProjection.configHash", 1 },
                    { "directProjection.sourceMembershipSignature", 1 },
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "fieldId", 1 },
                    { "bucketKey", 1 },
                    { "periodInstanceKey", 1 },
                    { "reportStatus", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "directProjection.generationId", new BsonDocument("$type", "string") }
                }
            ), ct);

            await MongoIndexEnsureHelper.MigrateExactOwnedIndexAsync(col, new IndexSpec(
                name: "ux_workReportFieldStatAggregates_scope_field_bucket_period_status_active",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "fieldId", 1 },
                    { "bucketKey", 1 },
                    { "periodInstanceKey", 1 },
                    { "reportStatus", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ux_workReportFieldStatAggregates_scope_field_bucket_period_status_active_legacy",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "fieldId", 1 },
                    { "bucketKey", 1 },
                    { "periodInstanceKey", 1 },
                    { "reportStatus", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "directProjection.generationId", BsonNull.Value }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportFieldStatAggregates_tree_read_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "showInTree", 1 },
                    { "periodInstanceKey", 1 },
                    { "reportStatus", 1 },
                    { "valueCount", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportFieldStatAggregates_field_read_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "fieldId", 1 },
                    { "bucketKey", 1 },
                    { "periodInstanceKey", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportFieldStatAggregates_window_read_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "fieldId", 1 },
                    { "bucketKey", 1 },
                    { "periodStartDate", 1 },
                    { "periodEndDate", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(col, new IndexSpec(
                name: "ix_workReportFieldStatAggregates_label_read_generation",
                key: new BsonDocument
                {
                    { "directProjection.generationId", 1 },
                    { "workId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "statisticLabelCodes", 1 },
                    { "periodInstanceKey", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureNotificationsAsync(
            IMongoCollection<UserNotification> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_notifications_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_notifications_recipient_event_active",
                key: new BsonDocument
                {
                    { "recipientUserId", 1 },
                    { "eventKey", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_notifications_recipient_unread_occurred_desc",
                key: new BsonDocument
                {
                    { "recipientUserId", 1 },
                    { "isDeleted", 1 },
                    { "readAtUtc", 1 },
                    { "occurredAtUtc", -1 },
                    { "_id", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_notifications_recipient_occurred_desc",
                key: new BsonDocument
                {
                    { "recipientUserId", 1 },
                    { "isDeleted", 1 },
                    { "occurredAtUtc", -1 },
                    { "_id", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_notifications_source_lookup",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "workAssignmentId", 1 },
                    { "workReportPeriodId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private sealed record StatConfigIndexBinding(
            string Collection,
            IndexSpec Spec,
            string[]? UniqueFields,
            BsonDocument? UniqueMatch);

        private static IReadOnlyList<StatConfigIndexBinding>
            BuildStatConfigIndexBindings(MongoOptions opt)
        {
            var stringType = new BsonDocument("$type", "string");
            var receiptTyped = new BsonDocument
            {
                { "ownerKind", stringType },
                { "ownerId", stringType },
                { "commandId", stringType }
            };
            var activeDedupe = new BsonDocument
            {
                { "isDeleted", false },
                { "dedupeKey", new BsonDocument("$type", "string") }
            };
            var activeCorrelation = new BsonDocument
            {
                { "isDeleted", false },
                { "correlationId", new BsonDocument("$type", "string") }
            };
            var activeOnly = new BsonDocument
            {
                { "isDeleted", false },
                { "isActive", true }
            };
            var notDeleted = new BsonDocument("isDeleted", false);

            return new StatConfigIndexBinding[]
            {
                new(
                    opt.StatConfigCommandReceiptCollection,
                    new IndexSpec(
                        "ux_statConfigCommandReceipts_owner_command",
                        new BsonDocument
                        {
                            { "ownerKind", 1 },
                            { "ownerId", 1 },
                            { "commandId", 1 }
                        },
                        unique: true,
                        partial: receiptTyped),
                    new[] { "ownerKind", "ownerId", "commandId" },
                    receiptTyped),
                new(
                    opt.StatConfigCommandReceiptCollection,
                    new IndexSpec(
                        "ix_statConfigCommandReceipts_owner_created",
                        new BsonDocument
                        {
                            { "ownerKind", 1 },
                            { "ownerId", 1 },
                            { "createdAtUtc", -1 }
                        }),
                    null,
                    null),
                new(
                    opt.StatConfigValidationJobCollection,
                    new IndexSpec(
                        "ux_statConfigValidationJobs_dedupe",
                        new BsonDocument("dedupeKey", 1),
                        unique: true,
                        partial: activeDedupe),
                    new[] { "dedupeKey" },
                    activeDedupe),
                new(
                    opt.StatConfigValidationJobCollection,
                    new IndexSpec(
                        "ux_statConfigValidationJobs_correlation",
                        new BsonDocument("correlationId", 1),
                        unique: true,
                        partial: activeCorrelation),
                    new[] { "correlationId" },
                    activeCorrelation),
                new(
                    opt.StatConfigValidationJobCollection,
                    new IndexSpec(
                        "ix_statConfigValidationJobs_claim",
                        new BsonDocument
                        {
                            { "queueName", 1 },
                            { "isActive", 1 },
                            { "status", 1 },
                            { "nextRetryAtUtc", 1 },
                            { "leaseUntilUtc", 1 },
                            { "createdAtUtc", 1 }
                        },
                        partial: activeOnly),
                    null,
                    null),
                new(
                    opt.StatConfigValidationJobCollection,
                    new IndexSpec(
                        "ix_statConfigValidationJobs_owner_history",
                        new BsonDocument
                        {
                            { "ownerKind", 1 },
                            { "ownerId", 1 },
                            { "configId", 1 },
                            { "versionNo", -1 },
                            { "createdAtUtc", -1 }
                        },
                        partial: notDeleted),
                    null,
                    null),
                new(
                    opt.StatConfigValidationJobCollection,
                    new IndexSpec(
                        "ix_statConfigValidationJobs_ttl",
                        new BsonDocument("expiresAtUtc", 1),
                        expireAfterSeconds: 0),
                    null,
                    null),
                new(
                    opt.StatConfigAuditOutboxCollection,
                    new IndexSpec(
                        "ux_statConfigAuditOutbox_dedupe",
                        new BsonDocument("dedupeKey", 1),
                        unique: true,
                        partial: activeDedupe),
                    new[] { "dedupeKey" },
                    activeDedupe),
                new(
                    opt.StatConfigAuditOutboxCollection,
                    new IndexSpec(
                        "ix_statConfigAuditOutbox_due",
                        new BsonDocument
                        {
                            { "status", 1 },
                            { "nextAttemptAtUtc", 1 },
                            { "leaseUntilUtc", 1 },
                            { "createdAtUtc", 1 }
                        },
                        partial: notDeleted),
                    null,
                    null),
                new(
                    opt.StatConfigAuditOutboxCollection,
                    new IndexSpec(
                        "ix_statConfigAuditOutbox_target",
                        new BsonDocument
                        {
                            { "targetKind", 1 },
                            { "targetId", 1 },
                            { "createdAtUtc", -1 }
                        },
                        partial: notDeleted),
                    null,
                    null),
                new(
                    opt.StatConfigAuditOutboxCollection,
                    new IndexSpec(
                        "ix_statConfigAuditOutbox_ttl",
                        new BsonDocument("expiresAtUtc", 1),
                        expireAfterSeconds: 0),
                    null,
                    null)
            };
        }

        private static async Task EnsureStatConfigOperationsIndexesAsync(
            IMongoDatabase db,
            MongoOptions opt,
            CancellationToken ct)
        {
            foreach (var binding in BuildStatConfigIndexBindings(opt))
            {
                var collection = db.GetCollection<BsonDocument>(
                    binding.Collection);
                if (binding.UniqueFields is not null &&
                    binding.UniqueMatch is not null)
                {
                    await MongoIndexPrecheckHelper.PrecheckUniqueByFieldsAsync(
                        collection,
                        binding.UniqueFields,
                        binding.UniqueMatch,
                        ct);
                }

                await MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync(
                    collection,
                    binding.Spec,
                    ct);
            }
        }

        public static async Task<IReadOnlyList<MongoIndexContractStatus>>
            ValidateStatConfigIndexesAsync(
                IMongoDatabase db,
                MongoOptions opt,
                CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(db);
            ArgumentNullException.ThrowIfNull(opt);
            var result = new List<MongoIndexContractStatus>();
            foreach (var group in BuildStatConfigIndexBindings(opt)
                         .GroupBy(x => x.Collection, StringComparer.Ordinal))
            {
                var collection = db.GetCollection<BsonDocument>(group.Key);
                var current = await ListP8IndexDocsAsync(
                    collection,
                    ct);
                foreach (var binding in group)
                {
                    var actual = current.FirstOrDefault(document =>
                        document.TryGetValue("name", out var name) &&
                        name.IsString &&
                        string.Equals(
                            name.AsString,
                            binding.Spec.Name,
                            StringComparison.Ordinal));
                    result.Add(new MongoIndexContractStatus(
                        binding.Collection,
                        binding.Spec.Name,
                        new BsonDocument(binding.Spec.Key),
                        binding.Spec.Unique,
                        binding.Spec.Partial is null
                            ? null
                            : new BsonDocument(binding.Spec.Partial),
                        binding.Spec.ExpireAfterSeconds,
                        actual is not null &&
                        IsSameP8IndexSpec(
                            actual,
                            binding.Spec)));
                }
            }
            return result;
        }

        private static async Task<BsonDocument[]> ListP8IndexDocsAsync<T>(
            IMongoCollection<T> collection,
            CancellationToken ct)
        {
            using var cursor = await collection.Indexes.ListAsync(ct);
            return (await cursor.ToListAsync(ct)).ToArray();
        }

        private static bool IsSameP8IndexSpec(
            BsonDocument current,
            IndexSpec desired)
        {
            if (!current.TryGetValue("key", out var key) ||
                !key.IsBsonDocument ||
                !BsonEqualsP8Key(key.AsBsonDocument, desired.Key))
                return false;

            if (HasUnexpectedP8IndexOptions(current))
                return false;

            var currentUnique = current.TryGetValue("unique", out var unique) &&
                                unique.IsBoolean && unique.AsBoolean;
            if (currentUnique != desired.Unique)
                return false;

            var currentPartial = current.TryGetValue(
                    "partialFilterExpression",
                    out var partial) && partial.IsBsonDocument
                ? partial.AsBsonDocument
                : null;
            if (desired.Partial is null != (currentPartial is null) ||
                desired.Partial is not null &&
                !desired.Partial.Equals(currentPartial))
                return false;

            var currentExpire = current.TryGetValue(
                    "expireAfterSeconds",
                    out var expire)
                ? (int?)expire.ToInt32()
                : null;
            return currentExpire == desired.ExpireAfterSeconds;
        }

        private static bool BsonEqualsP8Key(
            BsonDocument left,
            BsonDocument right)
        {
            if (left.ElementCount != right.ElementCount)
                return false;
            var leftElements = left.Elements.ToArray();
            var rightElements = right.Elements.ToArray();
            for (var index = 0; index < leftElements.Length; index++)
            {
                if (leftElements[index].Name != rightElements[index].Name ||
                    !leftElements[index].Value.Equals(
                        rightElements[index].Value))
                    return false;
            }
            return true;
        }

        private static bool HasUnexpectedP8IndexOptions(
            BsonDocument current)
        {
            if (current.TryGetValue("sparse", out var sparse) &&
                sparse.IsBoolean && sparse.AsBoolean)
                return true;
            if (current.TryGetValue("hidden", out var hidden) &&
                hidden.IsBoolean && hidden.AsBoolean)
                return true;
            return current.Contains("collation") ||
                   current.Contains("wildcardProjection") ||
                   current.Contains("storageEngine") ||
                   current.Contains("weights") ||
                   current.Contains("default_language") ||
                   current.Contains("language_override");
        }

        private sealed class IndexSpec
        {
            public IndexSpec(
                string name,
                BsonDocument key,
                bool unique = false,
                BsonDocument? partial = null,
                int? expireAfterSeconds = null)
            {
                Name = name;
                Key = key;
                Unique = unique;
                Partial = partial;
                ExpireAfterSeconds = expireAfterSeconds;
            }

            public string Name { get; }
            public BsonDocument Key { get; }
            public bool Unique { get; }
            public BsonDocument? Partial { get; }
            public int? ExpireAfterSeconds { get; }
        }

        private static class MongoIndexEnsureHelper
        {
            public static async Task MigrateExactOwnedIndexAsync<T>(
                IMongoCollection<T> col,
                IndexSpec expectedLegacy,
                CancellationToken ct)
                => await MigrateExactOwnedIndexAsync(
                    col,
                    expectedLegacy,
                    null,
                    ct);

            public static async Task MigrateExactOwnedIndexAsync<T>(
                IMongoCollection<T> col,
                IndexSpec expectedLegacy,
                IndexSpec? acceptedReplacement,
                CancellationToken ct)
            {
                if (acceptedReplacement is not null &&
                    acceptedReplacement.Name != expectedLegacy.Name)
                {
                    throw new InvalidOperationException(
                        "An accepted migrated replacement must retain the legacy index name.");
                }
                var docs = await ListIndexDocsAsync(col, ct);
                var current = docs.FirstOrDefault(document =>
                    document.TryGetValue("name", out var name) &&
                    name.IsString &&
                    name.AsString == expectedLegacy.Name);
                if (current is null)
                    return;
                if (acceptedReplacement is not null &&
                    IsSameSpec(current, acceptedReplacement))
                    return;
                if (!IsSameSpec(current, expectedLegacy))
                {
                    throw P8IndexConfigurationInvalid(
                        "legacyIndexMigrationSpecMismatch",
                        col.CollectionNamespace.CollectionName,
                        expectedLegacy.Name);
                }

                // Drop is authorized only after exact validation of the known
                // legacy owner. An exact replacement already returned above.
                await col.Indexes.DropOneAsync(expectedLegacy.Name, ct);
            }

            public static async Task DropIfExistsAsync<T>(
                IMongoCollection<T> col,
                string name,
                CancellationToken ct)
            {
                var docs = await ListIndexDocsAsync(col, ct);
                if (docs.Any(d => d.TryGetValue("name", out var n) && n.IsString && n.AsString == name))
                    await col.Indexes.DropOneAsync(name, ct);
            }

            public static async Task EnsureBySpecAsync<T>(
                IMongoCollection<T> col,
                IndexSpec desired,
                CancellationToken ct)
            {
                await DropConflictsByKeyAsync(col, desired, ct);

                var docs = await ListIndexDocsAsync(col, ct);
                var current = docs.FirstOrDefault(d => d["name"].AsString == desired.Name);
                if (current != null && !IsSameSpec(current, desired))
                    await col.Indexes.DropOneAsync(desired.Name, ct);

                await CreateIndexByCommandAsync(col, desired, ct);
            }

            public static async Task EnsureFailClosedBySpecAsync<T>(
                IMongoCollection<T> col,
                IndexSpec desired,
                CancellationToken ct)
            {
                var docs = await ListIndexDocsAsync(col, ct);
                var current = docs.FirstOrDefault(document =>
                    document.TryGetValue("name", out var name) &&
                    name.IsString &&
                    name.AsString == desired.Name);
                if (current is not null)
                {
                    if (IsSameSpec(current, desired))
                        return;
                    throw P8IndexConfigurationInvalid(
                        "ownedIndexSpecMismatch",
                        col.CollectionNamespace.CollectionName,
                        desired.Name);
                }

                var sameKey = docs.FirstOrDefault(document =>
                    document.TryGetValue("name", out var name) &&
                    name.IsString &&
                    name.AsString != "_id_" &&
                    document.TryGetValue("key", out var key) &&
                    key.IsBsonDocument &&
                    BsonEqualsKey(key.AsBsonDocument, desired.Key));
                if (sameKey is not null)
                {
                    throw P8IndexConfigurationInvalid(
                        "indexKeyOwnedByAnotherName",
                        col.CollectionNamespace.CollectionName,
                        desired.Name,
                        sameKey["name"].AsString);
                }

                await CreateIndexByCommandAsync(col, desired, ct);
                docs = await ListIndexDocsAsync(col, ct);
                current = docs.FirstOrDefault(document =>
                    document.TryGetValue("name", out var name) &&
                    name.IsString && name.AsString == desired.Name);
                if (current is null || !IsSameSpec(current, desired))
                    throw P8IndexConfigurationInvalid(
                        "createdIndexSpecMismatch",
                        col.CollectionNamespace.CollectionName,
                        desired.Name);
            }

            private static async Task DropConflictsByKeyAsync<T>(
                IMongoCollection<T> col,
                IndexSpec desired,
                CancellationToken ct)
            {
                var docs = await ListIndexDocsAsync(col, ct);

                foreach (var d in docs)
                {
                    var name = d["name"].AsString;
                    if (name == "_id_") continue;
                    if (name == desired.Name) continue;

                    if (!d.TryGetValue("key", out var k) || !k.IsBsonDocument)
                        continue;

                    var keyDoc = k.AsBsonDocument;
                    if (BsonEqualsKey(keyDoc, desired.Key))
                        await col.Indexes.DropOneAsync(name, ct);
                }
            }

            private static async Task<BsonDocument[]> ListIndexDocsAsync<T>(
                IMongoCollection<T> col,
                CancellationToken ct)
            {
                using var cursor = await col.Indexes.ListAsync(ct);
                var list = await cursor.ToListAsync(ct);
                return list.ToArray();
            }

            private static bool IsSameSpec(BsonDocument current, IndexSpec desired)
            {
                if (!current.TryGetValue("key", out var k) || !k.IsBsonDocument)
                    return false;

                if (!BsonEqualsKey(k.AsBsonDocument, desired.Key))
                    return false;

                if (HasUnexpectedP8IndexOptions(current))
                    return false;

                var curUnique = current.TryGetValue("unique", out var u) && u.IsBoolean && u.AsBoolean;
                if (curUnique != desired.Unique)
                    return false;

                var curPartial = current.TryGetValue("partialFilterExpression", out var p) && p.IsBsonDocument
                    ? p.AsBsonDocument
                    : null;

                if (desired.Partial == null && curPartial != null)
                    return false;
                if (desired.Partial != null && curPartial == null)
                    return false;
                if (desired.Partial != null && !desired.Partial.Equals(curPartial))
                    return false;

                var curExpire = current.TryGetValue("expireAfterSeconds", out var e)
                    ? (int?)e.ToInt32()
                    : null;

                if (desired.ExpireAfterSeconds == null && curExpire != null)
                    return false;
                if (desired.ExpireAfterSeconds != null && curExpire == null)
                    return false;
                if (desired.ExpireAfterSeconds != null && curExpire != desired.ExpireAfterSeconds)
                    return false;

                return true;
            }

            private static bool BsonEqualsKey(BsonDocument a, BsonDocument b)
            {
                if (a.ElementCount != b.ElementCount)
                    return false;

                var ae = a.Elements.ToArray();
                var be = b.Elements.ToArray();

                for (int i = 0; i < ae.Length; i++)
                {
                    if (ae[i].Name != be[i].Name)
                        return false;

                    if (!ae[i].Value.Equals(be[i].Value))
                        return false;
                }

                return true;
            }

            private static async Task CreateIndexByCommandAsync<T>(
                IMongoCollection<T> col,
                IndexSpec desired,
                CancellationToken ct)
            {
                var idx = new BsonDocument
                {
                    { "name", desired.Name },
                    { "key", desired.Key }
                };

                if (desired.Unique)
                    idx.Add("unique", true);

                if (desired.Partial != null)
                    idx.Add("partialFilterExpression", desired.Partial);

                if (desired.ExpireAfterSeconds != null)
                    idx.Add("expireAfterSeconds", desired.ExpireAfterSeconds.Value);

                var cmd = new BsonDocument
                {
                    { "createIndexes", col.CollectionNamespace.CollectionName },
                    { "indexes", new BsonArray { idx } }
                };

                await col.Database.RunCommandAsync<BsonDocument>(cmd, cancellationToken: ct);
            }
        }

        private static tdtd_be.Common.Errors.AppException
            P8IndexConfigurationInvalid(
                string reason,
                string collection,
                string desiredName,
                string? conflictingName = null)
            => tdtd_be.Common.Errors.AppExceptionFactory.Create(
                tdtd_be.Common.Errors.AppErrorCode.INDEX_CONFIGURATION_INVALID,
                new
                {
                    reason,
                    collection,
                    desiredName,
                    conflictingName,
                    owner = nameof(MongoIndexInitializer),
                    mutation = "NONE"
                });

        private static class MongoIndexPrecheckHelper
        {
            public static async Task PrecheckUniqueByFieldAsync<T>(
                IMongoCollection<T> col,
                string field,
                BsonDocument matchFilter,
                CancellationToken ct)
            {
                if (string.IsNullOrWhiteSpace(field))
                    throw IndexConfigurationInvalid("fieldRequired", new { field = nameof(field) });

                if (matchFilter == null)
                    throw IndexConfigurationInvalid("matchFilterRequired", new { field = nameof(matchFilter) });

                var effectiveMatch = new BsonDocument(matchFilter)
                {
                    [field] = new BsonDocument("$exists", true)
                };

                var pipeline = new[]
                {
                    new BsonDocument("$match", effectiveMatch),
                    new BsonDocument("$group", new BsonDocument
                    {
                        { "_id", "$" + field },
                        { "count", new BsonDocument("$sum", 1) }
                    }),
                    new BsonDocument("$match", new BsonDocument("count", new BsonDocument("$gt", 1))),
                    new BsonDocument("$limit", 10)
                };

                var dup = await col.Aggregate<BsonDocument>(pipeline).ToListAsync(ct);
                if (dup.Count > 0)
                {
                    var sample = string.Join(", ", dup.Select(x =>
                        $"{field}={x["_id"]} (count={x["count"]})"));

                    throw IndexConfigurationInvalid("duplicateUniqueKey", new
                    {
                        collection = col.CollectionNamespace.CollectionName,
                        field,
                        sample
                    });
                }
            }

            public static async Task PrecheckUniqueByFieldsAsync<T>(
                IMongoCollection<T> col,
                string[] fields,
                BsonDocument matchFilter,
                CancellationToken ct)
            {
                if (fields == null || fields.Length == 0)
                    throw IndexConfigurationInvalid("fieldsRequired", new { field = nameof(fields) });

                if (matchFilter == null)
                    throw IndexConfigurationInvalid("matchFilterRequired", new { field = nameof(matchFilter) });

                var groupId = new BsonDocument();
                foreach (var f in fields)
                    groupId.Add(f, "$" + f);

                var pipeline = new[]
                {
                    new BsonDocument("$match", matchFilter),
                    new BsonDocument("$group", new BsonDocument
                    {
                        { "_id", groupId },
                        { "count", new BsonDocument("$sum", 1) }
                    }),
                    new BsonDocument("$match", new BsonDocument("count", new BsonDocument("$gt", 1))),
                    new BsonDocument("$limit", 10)
                };

                var dup = await col.Aggregate<BsonDocument>(pipeline).ToListAsync(ct);
                if (dup.Count > 0)
                {
                    var keyText = string.Join(", ", fields);
                    var sample = string.Join("; ", dup.Select(x =>
                    {
                        var id = x["_id"].AsBsonDocument;
                        var parts = string.Join(", ", fields.Select(f =>
                            $"{f}={id.GetValue(f, BsonNull.Value)}"));
                        return $"{parts} (count={x["count"]})";
                    }));

                    throw IndexConfigurationInvalid("duplicateUniqueKey", new
                    {
                        collection = col.CollectionNamespace.CollectionName,
                        fields,
                        keyText,
                        sample
                    });
                }
            }

            private static tdtd_be.Common.Errors.AppException IndexConfigurationInvalid(string reason, object? details = null)
                => tdtd_be.Common.Errors.AppExceptionFactory.Create(
                    tdtd_be.Common.Errors.AppErrorCode.INDEX_CONFIGURATION_INVALID,
                    new { reason, details });
        }
    }
}
