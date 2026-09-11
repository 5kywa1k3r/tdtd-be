using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsConfiguration;
using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Data
{
    public sealed class MongoDbContext
    {
        public IMongoDatabase Db { get; }
        public MongoOptions Options { get; }

        public IMongoCollection<AppUser> Users { get; }
        public IMongoCollection<RefreshTokenDoc> RefreshTokens { get; }
        public IMongoCollection<Unit> Units { get; }
        public IMongoCollection<UnitType> UnitTypes { get; }
        public IMongoCollection<Position> Positions { get; }
        public IMongoCollection<UnitVersionHistory> UnitHistories { get; }
        public IMongoCollection<FileDoc> Files { get; }
        public IMongoCollection<DynamicExcelTemplate> DynamicExcelTemplates { get; }
        public IMongoCollection<DynamicFormTemplate> DynamicFormTemplates { get; }
        public IMongoCollection<DynamicFormSectionDocument> DynamicFormSections { get; }
        public IMongoCollection<LabelCatalogItem> Labels { get; }
        public IMongoCollection<StatConfigCommandReceipt> StatConfigCommandReceipts { get; }
        public IMongoCollection<StatConfigValidationJob> StatConfigValidationJobs { get; }
        public IMongoCollection<StatConfigAuditOutboxItem> StatConfigAuditOutbox { get; }
        public IMongoCollection<LabelEnumCatalog> LabelEnumCatalogs { get; }
        public IMongoCollection<LabelEnumOptionReadModel> LabelEnumOptionReadModels { get; }
        public IMongoCollection<Work> Works { get; }
        public IMongoCollection<WorkHistory> WorkHistories { get; }
        public IMongoCollection<CounterDoc> Counters { get; }
        public IMongoCollection<WorkAssignment> WorkAssignments { get; }
        public IMongoCollection<WorkAssignmentAggregateConfig> WorkAssignmentAggregateConfigs { get; }
        public IMongoCollection<WorkAssignmentBasicSummaryConfig> WorkAssignmentBasicSummaryConfigs { get; }
        public IMongoCollection<WorkAssignmentBasicSummarySnapshot> WorkAssignmentBasicSummarySnapshots { get; }
        public IMongoCollection<WorkAssignmentAdvancedSummaryConfig> WorkAssignmentAdvancedSummaryConfigs { get; }
        public IMongoCollection<WorkAssignmentAdvancedSummaryDayNode> WorkAssignmentAdvancedSummaryDayNodes { get; }
        public IMongoCollection<WorkAssignmentAdvancedSummaryMonthNode> WorkAssignmentAdvancedSummaryMonthNodes { get; }
        public IMongoCollection<WorkAssignmentAdvancedSummaryYearNode> WorkAssignmentAdvancedSummaryYearNodes { get; }
        public IMongoCollection<WorkSummaryTokenLedger> WorkSummaryTokenLedgers { get; }
        public IMongoCollection<DynamicFlowTemplate> DynamicFlowTemplates { get; }
        public IMongoCollection<DynamicFlowTemplateVersion> DynamicFlowTemplateVersions { get; }
        public IMongoCollection<DynamicFlowDefinitionCommandReceipt> DynamicFlowDefinitionCommandReceipts { get; }
        public IMongoCollection<DynamicFlowEvent> DynamicFlowEvents { get; }
        public IMongoCollection<DynamicFlowInstance> DynamicFlowInstances { get; }
        public IMongoCollection<DynamicFlowStepInstance> DynamicFlowStepInstances { get; }
        public IMongoCollection<DynamicFlowParticipantSnapshot> DynamicFlowParticipantSnapshots { get; }
        public IMongoCollection<DynamicFlowRuntimeCommandReceipt> DynamicFlowRuntimeCommandReceipts { get; }
        public IMongoCollection<DynamicFlowRuntimeEvent> DynamicFlowRuntimeEvents { get; }
        public IMongoCollection<DynamicFlowRuntimeOutboxItem> DynamicFlowRuntimeOutbox { get; }
        public IMongoCollection<DynamicFlowMappingApplyReceipt> DynamicFlowMappingApplyReceipts { get; }
        public IMongoCollection<DynamicFlowMappingProvenanceRecord> DynamicFlowMappingProvenanceRecords { get; }
        public IMongoCollection<DynamicFlowMappingEvent> DynamicFlowMappingEvents { get; }
        public IMongoCollection<DynamicFlowMappingOutboxItem> DynamicFlowMappingOutbox { get; }
        public IMongoCollection<DynamicFlowGatewayInstance> DynamicFlowGatewayInstances { get; }
        public IMongoCollection<DynamicFlowGatewayContribution> DynamicFlowGatewayContributions { get; }
        public IMongoCollection<DynamicFlowPeriodicSchedule> DynamicFlowPeriodicSchedules { get; }
        public IMongoCollection<DynamicFlowPeriodicOccurrence> DynamicFlowPeriodicOccurrences { get; }
        public IMongoCollection<DynamicFlowExecutionEpoch> DynamicFlowExecutionEpochs { get; }
        public IMongoCollection<WorkTemplateAssignee> WorkTemplateAssignees { get; }
        public IMongoCollection<DocRole> DocRoles { get; }
        public IMongoCollection<WorkListDocRole> WorkListDocRoles { get; }
        public IMongoCollection<AssignmentListDocRole> AssignmentListDocRoles { get; }
        public IMongoCollection<MyReportTemplateListDocRole> MyReportTemplateListDocRoles { get; }
        public IMongoCollection<MyReportPeriodListDocRole> MyReportPeriodListDocRoles { get; }
        public IMongoCollection<ReviewReportListDocRole> ReviewReportListDocRoles { get; }
        public IMongoCollection<ReviewAssignmentSummaryDocRole> ReviewAssignmentSummaryDocRoles { get; }
        public IMongoCollection<DocRoleReadModelProjectionRetryJob> DocRoleReadModelProjectionRetryJobs { get; }
        public IMongoCollection<WorkAssignmentReport> WorkAssignmentReports { get; }
        public IMongoCollection<WorkAssignmentReportSection> WorkAssignmentReportSections { get; }
        public IMongoCollection<WorkReportPayload> WorkReportPayloads { get; }
        public IMongoCollection<WorkReportTableValue> WorkReportTableValues { get; }
        public IMongoCollection<WorkReportPeriod> WorkReportPeriods { get; }
        public IMongoCollection<WorkAssignmentReportLog> WorkAssignmentReportLogs { get; }
        public IMongoCollection<WorkAssignmentHandoverHistory> WorkAssignmentHandoverHistories { get; }
        public IMongoCollection<WorkStatusOperationLog> WorkStatusOperationLogs { get; }
        public IMongoCollection<DynamicFormCloneRequest> DynamicFormCloneRequests { get; }
        public IMongoCollection<UserActionLog> UserActionLogs { get; }
        public IMongoCollection<UserActionLogRetryJob> UserActionLogRetryJobs { get; }
        public IMongoCollection<WorkAssignmentQueueItem> WorkAssignmentQueueItems { get; }
        public IMongoCollection<WorkAssignmentEvaluationLog> WorkAssignmentEvaluationLogs { get; }
        public IMongoCollection<WorkAssignmentMaterializeJobs> WorkAssignmentMaterializeJobs { get; }
        public IMongoCollection<EvaluationTemplate> EvaluationTemplates { get; }
        public IMongoCollection<WorkReportLabelStatValue> WorkReportLabelStatValues { get; }
        public IMongoCollection<WorkReportLabelStatAggregate> WorkReportLabelStatAggregates { get; }
        public IMongoCollection<WorkReportTableStatValue> WorkReportTableStatValues { get; }
        public IMongoCollection<WorkReportTableStatAggregate> WorkReportTableStatAggregates { get; }
        public IMongoCollection<WorkReportFieldStatValue> WorkReportFieldStatValues { get; }
        public IMongoCollection<WorkReportFieldStatAggregate> WorkReportFieldStatAggregates { get; }
        public IMongoCollection<WorkReportStatisticRebuildJob> WorkReportStatisticRebuildJobs { get; }
        public IMongoCollection<StatisticReconciliationRun> StatisticReconciliationRuns { get; }
        public IMongoCollection<StatisticReconciliationObservation> StatisticReconciliationObservations { get; }
        public IMongoCollection<StatisticReconciliationReview> StatisticReconciliationReviews { get; }
        public IMongoCollection<WorkReportStatisticDiffConfig> WorkReportStatisticDiffConfigs { get; }
        public IMongoCollection<StatRunExportArtifact> WorkReportStatisticExports { get; }
        public IMongoCollection<StatRunExportArtifact> WorkReportStatisticDiffExports { get; }
        public IMongoCollection<UserNotification> Notifications { get; }
        public MongoDbContext(IOptions<MongoOptions> opt)
        {
            var o = opt.Value;
            Options = o;
            var client = new MongoClient(o.ConnectionString);
            Db = client.GetDatabase(o.Database);

            Users = Db.GetCollection<AppUser>(o.UserCollection);
            RefreshTokens = Db.GetCollection<RefreshTokenDoc>(o.RefreshTokenCollection);
            Units = Db.GetCollection<Unit>(o.UnitCollection);
            UnitTypes = Db.GetCollection<UnitType>(o.UnitTypeCollection);
            Positions = Db.GetCollection<Position>(o.PositionCollection);
            UnitHistories = Db.GetCollection<UnitVersionHistory>(o.UnitHistoryCollection);
            Files = Db.GetCollection<FileDoc>(o.FileDocCollection);
            DynamicExcelTemplates = Db.GetCollection<DynamicExcelTemplate>(o.DynamicExcelTemplateCollection);
            DynamicFormTemplates = Db.GetCollection<DynamicFormTemplate>(o.DynamicFormTemplateCollection);
            DynamicFormSections = Db.GetCollection<DynamicFormSectionDocument>(o.DynamicFormSectionCollection);
            Labels = Db.GetCollection<LabelCatalogItem>(o.LabelCollection);
            StatConfigCommandReceipts = Db.GetCollection<StatConfigCommandReceipt>(
                o.StatConfigCommandReceiptCollection);
            StatConfigValidationJobs = Db.GetCollection<StatConfigValidationJob>(
                o.StatConfigValidationJobCollection);
            StatConfigAuditOutbox = Db.GetCollection<StatConfigAuditOutboxItem>(
                o.StatConfigAuditOutboxCollection);
            LabelEnumCatalogs = Db.GetCollection<LabelEnumCatalog>(o.LabelEnumCatalogCollection);
            LabelEnumOptionReadModels = Db.GetCollection<LabelEnumOptionReadModel>(o.LabelEnumOptionReadModelCollection);
            Works = Db.GetCollection<Work>(o.WorkCollection);
            WorkHistories = Db.GetCollection<WorkHistory>(o.WorkHistoryCollection);
            Counters = Db.GetCollection<CounterDoc>(o.CounterCollection);
            WorkAssignments = Db.GetCollection<WorkAssignment>(o.WorkAssignmentCollection);
            WorkAssignmentAggregateConfigs = Db.GetCollection<WorkAssignmentAggregateConfig>(o.WorkAssignmentAggregateConfigCollection);
            WorkAssignmentBasicSummaryConfigs = Db.GetCollection<WorkAssignmentBasicSummaryConfig>(o.WorkAssignmentBasicSummaryConfigCollection);
            WorkAssignmentBasicSummarySnapshots = Db.GetCollection<WorkAssignmentBasicSummarySnapshot>(o.WorkAssignmentBasicSummarySnapshotCollection);
            WorkAssignmentAdvancedSummaryConfigs = Db.GetCollection<WorkAssignmentAdvancedSummaryConfig>(o.WorkAssignmentAdvancedSummaryConfigCollection);
            WorkAssignmentAdvancedSummaryDayNodes = Db.GetCollection<WorkAssignmentAdvancedSummaryDayNode>(o.WorkAssignmentAdvancedSummaryDayNodeCollection);
            WorkAssignmentAdvancedSummaryMonthNodes = Db.GetCollection<WorkAssignmentAdvancedSummaryMonthNode>(o.WorkAssignmentAdvancedSummaryMonthNodeCollection);
            WorkAssignmentAdvancedSummaryYearNodes = Db.GetCollection<WorkAssignmentAdvancedSummaryYearNode>(o.WorkAssignmentAdvancedSummaryYearNodeCollection);
            WorkSummaryTokenLedgers = Db.GetCollection<WorkSummaryTokenLedger>(o.WorkSummaryTokenLedgerCollection);
            DynamicFlowTemplates = Db.GetCollection<DynamicFlowTemplate>(o.DynamicFlowTemplateCollection);
            DynamicFlowTemplateVersions = Db.GetCollection<DynamicFlowTemplateVersion>(o.DynamicFlowTemplateVersionCollection);
            DynamicFlowDefinitionCommandReceipts = Db.GetCollection<DynamicFlowDefinitionCommandReceipt>(o.DynamicFlowDefinitionCommandReceiptCollection);
            DynamicFlowEvents = Db.GetCollection<DynamicFlowEvent>(o.DynamicFlowEventCollection);
            DynamicFlowInstances = Db.GetCollection<DynamicFlowInstance>(o.DynamicFlowInstanceCollection);
            DynamicFlowStepInstances = Db.GetCollection<DynamicFlowStepInstance>(o.DynamicFlowStepInstanceCollection);
            DynamicFlowParticipantSnapshots = Db.GetCollection<DynamicFlowParticipantSnapshot>(o.DynamicFlowParticipantSnapshotCollection);
            DynamicFlowRuntimeCommandReceipts = Db.GetCollection<DynamicFlowRuntimeCommandReceipt>(o.DynamicFlowRuntimeCommandReceiptCollection);
            DynamicFlowRuntimeEvents = Db.GetCollection<DynamicFlowRuntimeEvent>(o.DynamicFlowRuntimeEventCollection);
            DynamicFlowRuntimeOutbox = Db.GetCollection<DynamicFlowRuntimeOutboxItem>(o.DynamicFlowRuntimeOutboxCollection);
            DynamicFlowMappingApplyReceipts = Db.GetCollection<DynamicFlowMappingApplyReceipt>(o.DynamicFlowMappingApplyReceiptCollection);
            DynamicFlowMappingProvenanceRecords = Db.GetCollection<DynamicFlowMappingProvenanceRecord>(o.DynamicFlowMappingProvenanceCollection);
            DynamicFlowMappingEvents = Db.GetCollection<DynamicFlowMappingEvent>(o.DynamicFlowMappingEventCollection);
            DynamicFlowMappingOutbox = Db.GetCollection<DynamicFlowMappingOutboxItem>(o.DynamicFlowMappingOutboxCollection);
            DynamicFlowGatewayInstances = Db.GetCollection<DynamicFlowGatewayInstance>(o.DynamicFlowGatewayInstanceCollection);
            DynamicFlowGatewayContributions = Db.GetCollection<DynamicFlowGatewayContribution>(o.DynamicFlowGatewayContributionCollection);
            DynamicFlowPeriodicSchedules = Db.GetCollection<DynamicFlowPeriodicSchedule>(o.DynamicFlowPeriodicScheduleCollection);
            DynamicFlowPeriodicOccurrences = Db.GetCollection<DynamicFlowPeriodicOccurrence>(o.DynamicFlowPeriodicOccurrenceCollection);
            DynamicFlowExecutionEpochs = Db.GetCollection<DynamicFlowExecutionEpoch>(o.DynamicFlowExecutionEpochCollection);
            WorkTemplateAssignees = Db.GetCollection<WorkTemplateAssignee>(o.WorkTemplateAssigneeCollection);
            DocRoles = Db.GetCollection<DocRole>(o.DocRoleCollection);
            WorkListDocRoles = Db.GetCollection<WorkListDocRole>(o.WorkListDocRoleCollection);
            AssignmentListDocRoles = Db.GetCollection<AssignmentListDocRole>(o.AssignmentListDocRoleCollection);
            MyReportTemplateListDocRoles = Db.GetCollection<MyReportTemplateListDocRole>(o.MyReportTemplateListDocRoleCollection);
            MyReportPeriodListDocRoles = Db.GetCollection<MyReportPeriodListDocRole>(o.MyReportPeriodListDocRoleCollection);
            ReviewReportListDocRoles = Db.GetCollection<ReviewReportListDocRole>(o.ReviewReportListDocRoleCollection);
            ReviewAssignmentSummaryDocRoles = Db.GetCollection<ReviewAssignmentSummaryDocRole>(o.ReviewAssignmentSummaryDocRoleCollection);
            DocRoleReadModelProjectionRetryJobs = Db.GetCollection<DocRoleReadModelProjectionRetryJob>(o.DocRoleReadModelProjectionRetryJobCollection);
            WorkAssignmentReports = Db.GetCollection<WorkAssignmentReport>(o.WorkAssignmentReportCollection);
            WorkAssignmentReportSections = Db.GetCollection<WorkAssignmentReportSection>(o.WorkAssignmentReportSectionCollection);
            WorkReportPayloads = Db.GetCollection<WorkReportPayload>(o.WorkReportPayloadCollection);
            WorkReportTableValues = Db.GetCollection<WorkReportTableValue>(o.WorkReportTableValueCollection);
            WorkReportPeriods = Db.GetCollection<WorkReportPeriod>(o.WorkReportPeriodCollection);
            WorkAssignmentReportLogs = Db.GetCollection<WorkAssignmentReportLog>(o.WorkAssignmentReportLogCollection);
            WorkAssignmentHandoverHistories = Db.GetCollection<WorkAssignmentHandoverHistory>(o.WorkAssignmentHandoverHistoryCollection);
            WorkStatusOperationLogs = Db.GetCollection<WorkStatusOperationLog>(o.WorkStatusOperationLogCollection);
            DynamicFormCloneRequests = Db.GetCollection<DynamicFormCloneRequest>(o.DynamicFormCloneRequestCollection);
            UserActionLogs = Db.GetCollection<UserActionLog>(o.UserActionLogCollection);
            UserActionLogRetryJobs = Db.GetCollection<UserActionLogRetryJob>(o.UserActionLogRetryJobCollection);
            WorkAssignmentQueueItems = Db.GetCollection<WorkAssignmentQueueItem>(o.WorkAssignmentQueueCollection);
            WorkAssignmentEvaluationLogs = Db.GetCollection<WorkAssignmentEvaluationLog>(o.WorkAssignmentEvaluationLogCollection);
            WorkAssignmentMaterializeJobs = Db.GetCollection<WorkAssignmentMaterializeJobs>(o.WorkAssignmentMaterializeJobCollection);
            EvaluationTemplates = Db.GetCollection<EvaluationTemplate>(o.EvaluationTemplateCollection);
            WorkReportLabelStatValues = Db.GetCollection<WorkReportLabelStatValue>(o.WorkReportLabelStatValueCollection);
            WorkReportLabelStatAggregates = Db.GetCollection<WorkReportLabelStatAggregate>(o.WorkReportLabelStatAggregateCollection);
            WorkReportTableStatValues = Db.GetCollection<WorkReportTableStatValue>(o.WorkReportTableStatValueCollection);
            WorkReportTableStatAggregates = Db.GetCollection<WorkReportTableStatAggregate>(o.WorkReportTableStatAggregateCollection);
            WorkReportFieldStatValues = Db.GetCollection<WorkReportFieldStatValue>(o.WorkReportFieldStatValueCollection);
            WorkReportFieldStatAggregates = Db.GetCollection<WorkReportFieldStatAggregate>(o.WorkReportFieldStatAggregateCollection);
            WorkReportStatisticRebuildJobs = Db.GetCollection<WorkReportStatisticRebuildJob>(o.WorkReportStatisticRebuildJobCollection);
            StatisticReconciliationRuns = Db.GetCollection<StatisticReconciliationRun>(
                o.StatisticReconciliationRunCollection);
            StatisticReconciliationObservations = Db.GetCollection<StatisticReconciliationObservation>(
                o.StatisticReconciliationObservationCollection);
            StatisticReconciliationReviews = Db.GetCollection<StatisticReconciliationReview>(
                o.StatisticReconciliationReviewCollection);
            WorkReportStatisticDiffConfigs = Db.GetCollection<WorkReportStatisticDiffConfig>(o.WorkReportStatisticDiffConfigCollection);
            WorkReportStatisticExports = Db.GetCollection<StatRunExportArtifact>(o.WorkReportStatisticExportCollection);
            WorkReportStatisticDiffExports = Db.GetCollection<StatRunExportArtifact>(o.WorkReportStatisticDiffExportCollection);
            Notifications = Db.GetCollection<UserNotification>(o.NotificationCollection);
        }
    }
}
