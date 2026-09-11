using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService
{
    internal static bool IsApprovedMatchedRecheckBase(
        StatisticReconciliationRun run,
        StatisticReconciliationReview verdict)
        => run.Status == StatisticReconciliationRunStatuses.Matched &&
           verdict.Verdict ==
               StatisticReconciliationFinalVerdicts.Matched &&
           verdict.CompleteEvidence &&
           verdict.AllRequiredLayersZero &&
           verdict.Signable &&
           verdict.CloseoutAllowed &&
           !verdict.UnknownBlocksCloseout &&
           !verdict.MissingOrExtraIdentity;
}
