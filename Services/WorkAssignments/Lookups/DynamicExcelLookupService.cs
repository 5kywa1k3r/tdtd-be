using tdtd_be.Data;

namespace tdtd_be.Services.WorkAssignments.Lookups;

public sealed class DynamicExcelLookupService : IDynamicExcelLookupService
{
    private readonly MongoDbContext _ctx;

    public DynamicExcelLookupService(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<(string Code, string Name)> LoadAsync(string dynamicExcelId, CancellationToken ct = default)
    {
        // TODO: thay bằng collection thật của bệ hạ
        await Task.CompletedTask;

        var found = true;
        if (!found)
            throw new InvalidOperationException("Biểu mẫu/bảng động không tồn tại.");

        return ("DYN-001", "Biểu mẫu động");
    }
}