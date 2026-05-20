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
        public string PositionCollection { get; set; } = "positions";
        public string UnitHistoryCollection { get; set; } = "unit_histories";
        public string FileDocCollection { get; set; } = "file_doc";
        public string DynamicExcelTemplateCollection { get; set; } = "dynamic_excel_templates";
        public string DynamicFormTemplateCollection { get; set; } = "dynamic_form_templates";
        public string LabelCollection { get; set; } = "labels";
        public string WorkCollection { get; set; } = "works";
        public string WorkHistoryCollection { get; set; } = "work_histories";
        public string CounterCollection { get; set; } = "counters";
        public string WorkAssignmentCollection { get; set; } = "work_assignments";
        public string WorkTemplateAssigneeCollection { get; set; } = "work_template_assignees";
        public string DocRoleCollection { get; set; } = "doc_roles";
        public string WorkListDocRoleCollection { get; set; } = "work_list_doc_roles";
        public string AssignmentListDocRoleCollection { get; set; } = "assignment_list_doc_roles";
        public string MyReportTemplateListDocRoleCollection { get; set; } = "my_report_template_list_doc_roles";
        public string MyReportPeriodListDocRoleCollection { get; set; } = "my_report_period_list_doc_roles";
        public string ReviewReportListDocRoleCollection { get; set; } = "review_report_list_doc_roles";
        public string ReviewAssignmentSummaryDocRoleCollection { get; set; } = "review_assignment_summary_doc_roles";
        public string DocRoleReadModelProjectionRetryJobCollection { get; set; } = "docrole_read_model_projection_retry_jobs";
        public string WorkAssignmentReportCollection { get; set; } = "work_assignment_report";
        public string WorkReportPayloadCollection { get; set; } = "work_report_payloads";
        public string WorkReportTableValueCollection { get; set; } = "work_report_table_values";
        public string WorkReportPeriodCollection { get; set; } = "work_report_periods";
        public string WorkAssignmentReportLogCollection { get; set; } = "work_assignment_report_logs";
        public string WorkAssignmentHandoverHistoryCollection { get; set; } = "work_assignment_handover_histories";
        public string WorkStatusOperationLogCollection { get; set; } = "work_status_operation_logs";
        public string DynamicFormCloneRequestCollection { get; set; } = "dynamic_form_clone_requests";
        public string UserActionLogCollection { get; set; } = "user_action_logs";
        public string UserActionLogRetryJobCollection { get; set; } = "user_action_log_retry_jobs";
        public string WorkAssignmentQueueCollection { get; set; } = "work_assignment_queue";
        public string WorkAssignmentEvaluationLogCollection { get; set; } = "work_assignment_evaluation_logs";
        public string WorkAssignmentMaterializeJobCollection { get; set; } = "work_assignment_materialize_jobs";
        public string EvaluationTemplateCollection { get; set; } = "evaluation_templates";
        public string WorkReportLabelStatValueCollection { get; set; } = "work_report_label_stat_values";
        public string WorkReportLabelStatAggregateCollection { get; set; } = "work_report_label_stat_aggregates";
        public string WorkReportTableStatValueCollection { get; set; } = "work_report_table_stat_values";
        public string WorkReportTableStatAggregateCollection { get; set; } = "work_report_table_stat_aggregates";
        public string WorkReportFieldStatValueCollection { get; set; } = "work_report_field_stat_values";
        public string WorkReportFieldStatAggregateCollection { get; set; } = "work_report_field_stat_aggregates";
        public string WorkReportStatisticRebuildJobCollection { get; set; } = "work_report_statistic_rebuild_jobs";
        public string NotificationCollection { get; set; } = "notifications";
    }
}
