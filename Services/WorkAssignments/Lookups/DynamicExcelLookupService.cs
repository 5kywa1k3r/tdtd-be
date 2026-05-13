using MongoDB.Driver;
using tdtd_be.Common.Errors;
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
        var entity = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == dynamicExcelId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw AppExceptionFactory.NotFound(AppErrorCode.DYNAMIC_EXCEL_TEMPLATE_NOT_FOUND, new { dynamicExcelId });

        return (entity.Code ?? string.Empty, entity.Name ?? string.Empty);
    }
}
