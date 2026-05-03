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

            // LABELS
            var labels = db.GetCollection<LabelCatalogItem>(opt.LabelCollection);
            await EnsureLabelsAsync(labels, ct);

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

            // WORK TEMPLATE ASSIGNEES
            var workTemplateAssignees = db.GetCollection<WorkTemplateAssignee>(opt.WorkTemplateAssigneeCollection);
            await EnsureWorkTemplateAssigneesAsync(workTemplateAssignees, ct);

            // WORK ASSIGNMENT REPORTS
            var workAssignmentReports = db.GetCollection<WorkAssignmentReport>(opt.WorkAssignmentReportCollection);
            await EnsureWorkAssignmentReportsAsync(workAssignmentReports, ct);

            // WORK REPORT PERIODS
            var workReportPeriods = db.GetCollection<WorkReportPeriod>(opt.WorkReportPeriodCollection);
            await EnsureWorkReportPeriodsAsync(workReportPeriods, ct);

            // WORK ASSIGNMENT REPORT LOGS
            var workAssignmentReportLogs = db.GetCollection<WorkAssignmentReportLog>(opt.WorkAssignmentReportLogCollection);
            await EnsureWorkAssignmentReportLogsAsync(workAssignmentReportLogs, ct);

            // WORK STATUS OPERATION LOGS
            var workStatusOperationLogs = db.GetCollection<WorkStatusOperationLog>(opt.WorkStatusOperationLogCollection);
            await EnsureWorkStatusOperationLogsAsync(workStatusOperationLogs, ct);

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

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicExcel_labels_isDeleted",
                key: new BsonDocument
                {
                    { "labels", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        private static async Task EnsureDynamicFormsAsync(IMongoCollection<DynamicFormTemplate> col, CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_dynamicForms_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldAsync(
                col,
                field: "code",
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_dynamicForms_code_active",
                key: new BsonDocument("code", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
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
                name: "ix_dynamicForms_labels_isDeleted",
                key: new BsonDocument
                {
                    { "labels", 1 },
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
            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldsAsync(
                col,
                fields: new[] { "userId", "workId", "dynamicExcelId" },
                matchFilter: new BsonDocument("isDeleted", false),
                ct: ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_myReportTemplateListDocRoles_user_work_template_active",
                key: new BsonDocument
                {
                    { "userId", 1 },
                    { "workId", 1 },
                    { "dynamicExcelId", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
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

            await MongoIndexPrecheckHelper.PrecheckUniqueByFieldsAsync(
                col,
                fields: new[] { "workId", "dynamicFormTemplateId", "assigneeUserId" },
                matchFilter: new BsonDocument
                {
                    { "isDeleted", false },
                    { "isActive", true },
                    { "dynamicFormTemplateId", new BsonDocument
                        {
                            { "$exists", true },
                            { "$ne", BsonNull.Value }
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
                    { "assigneeUserId", 1 }
                },
                unique: true,
                partial: new BsonDocument
                {
                    { "isDeleted", false },
                    { "isActive", true },
                    { "dynamicFormTemplateId", new BsonDocument
                        {
                            { "$exists", true },
                            { "$ne", BsonNull.Value }
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
                    { "isDeleted", 1 },
                    { "updatedAtUtc", -1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_status_isDeleted_updatedAtUtc",
                key: new BsonDocument
                {
                    { "status", 1 },
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

        private static async Task EnsureWorkReportLabelStatValuesAsync(
            IMongoCollection<WorkReportLabelStatValue> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workReportLabelStatValues_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
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

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportLabelStatValues_work_period_label_status",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "periodInstanceKey", 1 },
                    { "labelCode", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportLabelStatValues_assignment_label_period",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "labelCode", 1 },
                    { "periodInstanceKey", 1 },
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

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
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

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportLabelStatAggregates_tree_read",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "periodInstanceKey", 1 },
                    { "reportStatus", 1 },
                    { "rowCount", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportLabelStatAggregates_label_read",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "labelCode", 1 },
                    { "periodInstanceKey", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
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

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workReportTableStatValues_report_metric_source_active",
                key: new BsonDocument
                {
                    { "workAssignmentReportId", 1 },
                    { "blockId", 1 },
                    { "metricKey", 1 },
                    { "sourceKey", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableStatValues_work_period_metric_status",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "periodInstanceKey", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "blockId", 1 },
                    { "metricKey", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableStatValues_assignment_metric_period",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "blockId", 1 },
                    { "metricKey", 1 },
                    { "periodInstanceKey", 1 },
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

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
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
                    { "periodInstanceKey", 1 },
                    { "reportStatus", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableStatAggregates_tree_read",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "scopeType", 1 },
                    { "scopeId", 1 },
                    { "periodInstanceKey", 1 },
                    { "reportStatus", 1 },
                    { "sum", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportTableStatAggregates_metric_read",
                key: new BsonDocument
                {
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
        }

        private static async Task EnsureWorkReportFieldStatValuesAsync(
            IMongoCollection<WorkReportFieldStatValue> col,
            CancellationToken ct)
        {
            await MongoIndexEnsureHelper.EnsureBySpecAsync(
                col,
                new IndexSpec("ix_workReportFieldStatValues_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
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

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportFieldStatValues_work_period_field_status",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "periodInstanceKey", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "fieldId", 1 },
                    { "bucketKey", 1 },
                    { "reportStatus", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportFieldStatValues_assignment_field_period",
                key: new BsonDocument
                {
                    { "workAssignmentId", 1 },
                    { "dynamicFormTemplateId", 1 },
                    { "fieldId", 1 },
                    { "periodInstanceKey", 1 },
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

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
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

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportFieldStatAggregates_tree_read",
                key: new BsonDocument
                {
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

            await MongoIndexEnsureHelper.EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workReportFieldStatAggregates_field_read",
                key: new BsonDocument
                {
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

        private static class MongoIndexPrecheckHelper
        {
            public static async Task PrecheckUniqueByFieldAsync<T>(
                IMongoCollection<T> col,
                string field,
                BsonDocument matchFilter,
                CancellationToken ct)
            {
                if (string.IsNullOrWhiteSpace(field))
                    throw new ArgumentException("field is required", nameof(field));

                if (matchFilter == null)
                    throw new ArgumentNullException(nameof(matchFilter));

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

                    throw new InvalidOperationException(
                        $"Duplicate documents detected in {col.CollectionNamespace.CollectionName} " +
                        $"for unique key: {field}. Samples: {sample}");
                }
            }

            public static async Task PrecheckUniqueByFieldsAsync<T>(
                IMongoCollection<T> col,
                string[] fields,
                BsonDocument matchFilter,
                CancellationToken ct)
            {
                if (fields == null || fields.Length == 0)
                    throw new ArgumentException("fields is required", nameof(fields));

                if (matchFilter == null)
                    throw new ArgumentNullException(nameof(matchFilter));

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

                    throw new InvalidOperationException(
                        $"Duplicate documents detected in {col.CollectionNamespace.CollectionName} " +
                        $"for unique key: {keyText}. Samples: {sample}");
                }
            }
        }
    }
}
