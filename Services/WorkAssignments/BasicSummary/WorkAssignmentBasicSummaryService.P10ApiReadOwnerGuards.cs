using System.Text.Json;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

namespace tdtd_be.Services.WorkAssignments.BasicSummary;

public sealed partial class WorkAssignmentBasicSummaryService
{
    internal static void P10ApiRequireExactSourceMembership(
        IReadOnlyCollection<string> frozenAssignmentIds,
        IReadOnlyCollection<string> frozenReportIds,
        IEnumerable<string> currentAssignmentIds,
        IEnumerable<string> currentReportIds)
    {
        var frozenAssignments = ExactPinnedObjectIds(
            frozenAssignmentIds,
            "API_BASIC_SOURCE_ASSIGNMENT_IDS");
        var frozenReports = ExactPinnedObjectIds(
            frozenReportIds,
            "API_BASIC_SOURCE_REPORT_IDS");
        var currentAssignments = ExactPinnedObjectIds(
            currentAssignmentIds,
            "API_BASIC_CURRENT_SOURCE_ASSIGNMENT_IDS");
        var currentReports = ExactPinnedObjectIds(
            currentReportIds,
            "API_BASIC_CURRENT_SOURCE_REPORT_IDS");

        if (!frozenAssignments.SequenceEqual(
                currentAssignments,
                StringComparer.Ordinal) ||
            !frozenReports.SequenceEqual(
                currentReports,
                StringComparer.Ordinal))
        {
            throw BasicApiSnapshotStale(
                "API_BASIC_SOURCE_MEMBERSHIP_DRIFT");
        }
    }

    internal static void P10ApiRequireValidSnapshotRequestJson(
        string? requestJson)
    {
        try
        {
            using var document = StatisticReconciliationActualJson.ParseStrict(
                requestJson,
                "API_BASIC_REQUEST_JSON");
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw BasicApiSnapshotStale(
                    "API_BASIC_REQUEST_JSON_INVALID");
            }
        }
        catch (StatisticReconciliationActualObservationException)
        {
            throw BasicApiSnapshotStale(
                "API_BASIC_REQUEST_JSON_INVALID");
        }
    }
}
