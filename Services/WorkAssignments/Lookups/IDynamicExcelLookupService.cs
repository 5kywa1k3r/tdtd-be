namespace tdtd_be.Services.WorkAssignments.Lookups;

public interface IDynamicExcelLookupService
{
    Task<(string Code, string Name)> LoadAsync(string dynamicExcelId, CancellationToken ct = default);
}