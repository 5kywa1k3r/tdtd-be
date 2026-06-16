using tdtd_be.DTOs.WorkAssignments.AggregateTable;

namespace tdtd_be.Services.WorkAssignments.Aggregate;

public interface IAggregateTableService
{
    Task<AggregateTableResponse> GetTableAsync(
        AggregateTableRequest req,
        CancellationToken ct);

    Task<DynamicFormAggregateResponse> GetDynamicFormAggregateAsync(
        DynamicFormAggregateRequest req,
        CancellationToken ct);

    Task<WorkAssignmentAggregateConfigDto?> GetAggregateConfigAsync(
        string assignmentId,
        CancellationToken ct);

    Task<WorkAssignmentAggregateConfigDto> SaveAggregateConfigAsync(
        string assignmentId,
        SaveWorkAssignmentAggregateConfigRequest req,
        CancellationToken ct);
}
