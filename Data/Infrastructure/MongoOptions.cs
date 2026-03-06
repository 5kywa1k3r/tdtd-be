namespace tdtd_be.Data.Infrastructure
{
    public sealed class MongoOptions
    {
        public string ConnectionString { get; init; } = null!;
        public string Database { get; init; } = null!;

        public string RefreshTokenCollection { get; set; } = "refresh_tokens";
        public string UnitCollection { get; set; } = "units";
        public string UserCollection { get; set; } = "users";
        public string UnitTypeCollection { get; set; } = "unit_types";
        public string UnitHistoryCollection { get; set; } = "unit_histories";
        public string FileDocCollection { get; set; } = "file_doc";
        public string DynamicExcelTemplateCollection { get; set; } = "dynamic_excel_templates";
        public string WorkCollection { get; set; } = "works";
        public string WorkHistoryCollection { get; set; } = "work_histories";
        public string CounterCollection { get; set; } = "counters";
        public string WorkAssignmentCollection { get; set; } = "work_assignments";
        public string DocRoleCollection { get; set; } = "doc_roles";
        public string WorkAssignmentReportCollection { get; set; } = "work_assignment_report";
    }
}