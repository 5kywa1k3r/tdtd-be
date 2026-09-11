using tdtd_be.DTOs.DynamicFlows;

namespace tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowMappingCallerRedaction
{
    public static void RedactSourceIdentities(
        DynamicFlowMappingPreviewResponse preview,
        IReadOnlySet<string> reportIdsWithoutRawAccess)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(reportIdsWithoutRawAccess);

        if (reportIdsWithoutRawAccess.Count == 0)
            return;

        foreach (var source in preview.SourceReports ?? [])
        {
            if (source.ReportId is null ||
                !reportIdsWithoutRawAccess.Contains(source.ReportId))
            {
                continue;
            }

            source.ReportId = null;
            source.WorkAssignmentId = null;
            source.FlowInstanceId = null;
            source.ExecutionEpoch = null;
            source.StepInstanceId = null;
            source.BranchId = null;
            source.AttemptNo = null;
            source.FlowStepId = null;
            source.FlowStepCode = null;
            source.DynamicFormTemplateId = null;
            source.FormFamilyId = null;
            source.FormVersionId = null;
            source.FormVersionNo = null;
            source.FormSchemaHash = null;
            source.PayloadRevision = null;
            source.PayloadHash = null;
            source.LifecycleRevision = null;
            source.LifecycleStatus = null;
            source.PeriodInstanceKey = null;
            source.IdentityRedacted = true;
        }

        foreach (var change in preview.Changes ?? [])
        {
            var directSourceRestricted =
                change.SourceReportId is not null &&
                reportIdsWithoutRawAccess.Contains(change.SourceReportId);
            if (directSourceRestricted)
            {
                change.SourceReportId = null;
                change.SourceKey = null;
            }

            foreach (var source in change.Sources ?? [])
            {
                var provenanceSourceRestricted =
                    directSourceRestricted ||
                    source.SourceReportId is not null &&
                    reportIdsWithoutRawAccess.Contains(source.SourceReportId);
                if (!provenanceSourceRestricted)
                {
                    continue;
                }

                source.InputKey = null;
                source.SourceDynamicFormTemplateId = null;
                source.SourceStepId = null;
                source.SourceStepCode = null;
                source.SourceAssignmentId = null;
                source.SourceReportId = null;
                source.SourcePayloadRevision = null;
                source.SourcePayloadHash = null;
                source.SourceLifecycleRevision = null;
                source.SourceKey = null;
                source.RowKey = null;
                source.ValueJson = null;
            }
        }

        preview.SummarySourceJson = null;
    }
}
