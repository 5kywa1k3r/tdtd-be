using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.Works;

public interface IWorkCodeGenerator
{
    Task<string> GenerateAsync(string username, int year, CancellationToken ct);
}

public sealed class WorkCodeGenerator : IWorkCodeGenerator
{
    private readonly MongoDbContext _ctx;

    public WorkCodeGenerator(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<string> GenerateAsync(string username, int year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_AUTOCODE_USERNAME_REQUIRED);

        var user = username.Trim();
        var key = $"work_autocode:{user}:{year}";

        var update = Builders<CounterDoc>.Update
            .Inc(x => x.Seq, 1)
            .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
            .SetOnInsert(x => x.Key, key);

        var opt = new FindOneAndUpdateOptions<CounterDoc>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After
        };

        var doc = await _ctx.Counters.FindOneAndUpdateAsync(
            filter: Builders<CounterDoc>.Filter.Eq(x => x.Key, key),
            update: update,
            options: opt,
            cancellationToken: ct);

        var seq = doc.Seq;
        return $"{user}{year}{seq:000000}";
    }
}
