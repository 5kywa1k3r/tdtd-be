namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class StatisticReconciliationActualCrossViewParityV2
{
    private static void RequireApiBaseTarget(
        NormalizedPlan plan,
        StatisticReconciliationActualCrossViewApiBaseProjection value)
    {
        if (!Eq(value.WorkId, plan.WorkId) ||
            !Eq(value.ScopeAssignmentId, plan.ScopeId) ||
            !Eq(value.DynamicFormTemplateId, plan.DynamicFormTemplateId) ||
            !Eq(value.PeriodInstanceKey, plan.PeriodInstanceKey) ||
            !Eq(value.AuthorizationSnapshotSha256,
                plan.AuthorizationSnapshotSha256))
        {
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .TargetMismatch);
        }
    }

    private static void RequireExportBaseTarget(
        NormalizedPlan plan,
        StatisticReconciliationActualCrossViewExportBaseProjection value)
    {
        if (!Eq(value.WorkId, plan.WorkId) ||
            !Eq(value.ScopeType, plan.ScopeType) ||
            !Eq(value.ScopeId, plan.ScopeId) ||
            !Eq(value.DynamicFormTemplateId, plan.DynamicFormTemplateId) ||
            !Eq(value.AuthorizationSnapshotSha256,
                plan.AuthorizationSnapshotSha256))
        {
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .TargetMismatch);
        }
    }
}
