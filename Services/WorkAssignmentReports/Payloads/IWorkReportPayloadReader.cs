using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignmentReports.Payloads;

public interface IWorkReportPayloadReader
{
    Task<WorkReportPayloadSnapshot> LoadReportPayloadAsync(
        WorkAssignmentReport report,
        CancellationToken ct = default);

    Task<string?> LoadReportTableBlockAsync(
        WorkAssignmentReport report,
        string blockId,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, string>> LoadReportTableBlocksAsync(
        WorkAssignmentReport report,
        IEnumerable<string> blockIds,
        CancellationToken ct = default);
}
