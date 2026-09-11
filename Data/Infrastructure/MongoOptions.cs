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
        public string DynamicFormSectionCollection { get; set; } = "dynamic_form_sections";
        public string LabelCollection { get; set; } = "labels";
        public string StatConfigCommandReceiptCollection { get; set; } =
            "stat_config_command_receipts";
        public string StatConfigValidationJobCollection { get; set; } =
            "stat_config_validation_jobs";
        public string StatConfigAuditOutboxCollection { get; set; } =
            "stat_config_audit_outbox";
        public string LabelEnumCatalogCollection { get; set; } = "label_enum_catalogs";
        public string LabelEnumOptionReadModelCollection { get; set; } = "label_enum_option_read_models";
        public string WorkCollection { get; set; } = "works";
        public string WorkHistoryCollection { get; set; } = "work_histories";
        public string CounterCollection { get; set; } = "counters";
        public string WorkAssignmentCollection { get; set; } = "work_assignments";
        public string WorkAssignmentAggregateConfigCollection { get; set; } = "work_assignment_aggregate_configs";
        public string WorkAssignmentBasicSummaryConfigCollection { get; set; } = "work_assignment_basic_summary_configs";
        public string WorkAssignmentBasicSummarySnapshotCollection { get; set; } = "work_assignment_basic_summary_snapshots";
        public string WorkAssignmentAdvancedSummaryConfigCollection { get; set; } = "work_assignment_advanced_summary_configs";
        public string WorkAssignmentAdvancedSummaryDayNodeCollection { get; set; } = "work_assignment_advanced_summary_day_nodes";
        public string WorkAssignmentAdvancedSummaryMonthNodeCollection { get; set; } = "work_assignment_advanced_summary_month_nodes";
        public string WorkAssignmentAdvancedSummaryYearNodeCollection { get; set; } = "work_assignment_advanced_summary_year_nodes";
        public string WorkSummaryTokenLedgerCollection { get; set; } = "work_summary_token_ledgers";
        public string DynamicFlowTemplateCollection { get; set; } = "dynamic_flow_templates";
        public string DynamicFlowTemplateVersionCollection { get; set; } = "dynamic_flow_template_versions";
        public string DynamicFlowDefinitionCommandReceiptCollection { get; set; } = "dynamic_flow_definition_command_receipts";
        public string DynamicFlowEventCollection { get; set; } = "dynamic_flow_events";
        public string DynamicFlowInstanceCollection { get; set; } = "dynamic_flow_instances";
        public string DynamicFlowStepInstanceCollection { get; set; } = "dynamic_flow_step_instances";
        public string DynamicFlowParticipantSnapshotCollection { get; set; } = "dynamic_flow_participant_snapshots";
        public string DynamicFlowRuntimeCommandReceiptCollection { get; set; } = "dynamic_flow_runtime_command_receipts";
        public string DynamicFlowRuntimeEventCollection { get; set; } = "dynamic_flow_runtime_events";
        public string DynamicFlowRuntimeOutboxCollection { get; set; } = "dynamic_flow_runtime_outbox";
        public string DynamicFlowMappingApplyReceiptCollection { get; set; } = "dynamic_flow_mapping_apply_receipts";
        public string DynamicFlowMappingProvenanceCollection { get; set; } = "dynamic_flow_mapping_provenance";
        public string DynamicFlowMappingEventCollection { get; set; } = "dynamic_flow_mapping_events";
        public string DynamicFlowMappingOutboxCollection { get; set; } = "dynamic_flow_mapping_outbox";
        public string DynamicFlowGatewayInstanceCollection { get; set; } = "dynamic_flow_gateway_instances";
        public string DynamicFlowGatewayContributionCollection { get; set; } = "dynamic_flow_gateway_contributions";
        public string DynamicFlowPeriodicScheduleCollection { get; set; } = "dynamic_flow_periodic_schedules";
        public string DynamicFlowPeriodicOccurrenceCollection { get; set; } = "dynamic_flow_periodic_occurrences";
        public string DynamicFlowExecutionEpochCollection { get; set; } = "dynamic_flow_execution_epochs";
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
        public string WorkAssignmentReportSectionCollection { get; set; } = "work_assignment_report_sections";
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
        public string StatisticReconciliationRunCollection { get; set; } =
            "work_report_statistic_reconciliations";
        public string StatisticReconciliationObservationCollection { get; set; } =
            "work_report_statistic_reconciliation_observations";
        public string StatisticReconciliationReviewCollection { get; set; } =
            "work_report_statistic_reconciliation_reviews";
        public string WorkReportStatisticDiffConfigCollection { get; set; } = "work_report_statistic_diff_configs";
        public string WorkReportStatisticExportCollection { get; set; } = "work_report_statistic_exports";
        public string WorkReportStatisticDiffExportCollection { get; set; } = "work_report_statistic_diff_exports";
        public string NotificationCollection { get; set; } = "notifications";
    }
}
