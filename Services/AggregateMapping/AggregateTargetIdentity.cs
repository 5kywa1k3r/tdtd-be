using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

// A view is a target, never a fabricated report or assignment. Report wire identity
// and legacy hashes are unchanged when View is absent.
internal static class AggregateTargetIdentity
{
    internal static string? Id(AggregatePeriodContextDto context) => context.View?.ViewId ?? context.ReportId;
    internal static string LockKind(AggregatePeriodContextDto context) => context.View == null ? "REPORT" : "VIEW";
    internal static string? Parent(AggregatePeriodContextDto context) => context.View?.Kind == "WORK" ? null : context.AssignmentId;
}
