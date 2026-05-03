using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Data
{
    public sealed class MongoDbContext
    {
        public IMongoDatabase Db { get; }

        public IMongoCollection<AppUser> Users { get; }
        public IMongoCollection<RefreshTokenDoc> RefreshTokens { get; }
        public IMongoCollection<Unit> Units { get; }
        public IMongoCollection<UnitType> UnitTypes { get; }
        public IMongoCollection<Position> Positions { get; }
        public IMongoCollection<UnitVersionHistory> UnitHistories { get; }
        public IMongoCollection<FileDoc> Files { get; }
        public IMongoCollection<DynamicExcelTemplate> DynamicExcelTemplates { get; }
        public IMongoCollection<DynamicFormTemplate> DynamicFormTemplates { get; }
        public IMongoCollection<LabelCatalogItem> Labels { get; }
        public IMongoCollection<Work> Works { get; }
        public IMongoCollection<WorkHistory> WorkHistories { get; }
        public IMongoCollection<CounterDoc> Counters { get; }
        public IMongoCollection<WorkAssignment> WorkAssignments { get; }
        public IMongoCollection<WorkTemplateAssignee> WorkTemplateAssignees { get; }
        public IMongoCollection<DocRole> DocRoles { get; }
        public IMongoCollection<WorkListDocRole> WorkListDocRoles { get; }
        public IMongoCollection<AssignmentListDocRole> AssignmentListDocRoles { get; }
        public IMongoCollection<MyReportTemplateListDocRole> MyReportTemplateListDocRoles { get; }
        public IMongoCollection<MyReportPeriodListDocRole> MyReportPeriodListDocRoles { get; }
        public IMongoCollection<ReviewReportListDocRole> ReviewReportListDocRoles { get; }
        public IMongoCollection<WorkAssignmentReport> WorkAssignmentReports { get; }
        public IMongoCollection<WorkReportPeriod> WorkReportPeriods { get; }
        public IMongoCollection<WorkAssignmentReportLog> WorkAssignmentReportLogs { get; }
        public IMongoCollection<WorkStatusOperationLog> WorkStatusOperationLogs { get; }
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
        public MongoDbContext(IOptions<MongoOptions> opt)
        {
            var o = opt.Value;
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
            Labels = Db.GetCollection<LabelCatalogItem>(o.LabelCollection);
            Works = Db.GetCollection<Work>(o.WorkCollection);
            WorkHistories = Db.GetCollection<WorkHistory>(o.WorkHistoryCollection);
            Counters = Db.GetCollection<CounterDoc>(o.CounterCollection);
            WorkAssignments = Db.GetCollection<WorkAssignment>(o.WorkAssignmentCollection);
            WorkTemplateAssignees = Db.GetCollection<WorkTemplateAssignee>(o.WorkTemplateAssigneeCollection);
            DocRoles = Db.GetCollection<DocRole>(o.DocRoleCollection);
            WorkListDocRoles = Db.GetCollection<WorkListDocRole>(o.WorkListDocRoleCollection);
            AssignmentListDocRoles = Db.GetCollection<AssignmentListDocRole>(o.AssignmentListDocRoleCollection);
            MyReportTemplateListDocRoles = Db.GetCollection<MyReportTemplateListDocRole>(o.MyReportTemplateListDocRoleCollection);
            MyReportPeriodListDocRoles = Db.GetCollection<MyReportPeriodListDocRole>(o.MyReportPeriodListDocRoleCollection);
            ReviewReportListDocRoles = Db.GetCollection<ReviewReportListDocRole>(o.ReviewReportListDocRoleCollection);
            WorkAssignmentReports = Db.GetCollection<WorkAssignmentReport>(o.WorkAssignmentReportCollection);
            WorkReportPeriods = Db.GetCollection<WorkReportPeriod>(o.WorkReportPeriodCollection);
            WorkAssignmentReportLogs = Db.GetCollection<WorkAssignmentReportLog>(o.WorkAssignmentReportLogCollection);
            WorkStatusOperationLogs = Db.GetCollection<WorkStatusOperationLog>(o.WorkStatusOperationLogCollection);
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
        }
    }
}
