using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.Works
{
    public interface IWorkHistoryService
    {
        Task AppendAsync(string workId, string byUserId, WorkHistoryType type, Dictionary<string, object>? data, CancellationToken ct);
    }

    public sealed class WorkHistoryService : IWorkHistoryService
    {
        private readonly MongoDbContext _ctx;

        public WorkHistoryService(MongoDbContext ctx)
        {
            _ctx = ctx;
        }

        public async Task AppendAsync(string workId, string byUserId, WorkHistoryType type, Dictionary<string, object>? data, CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            var doc = new WorkHistory
            {
                WorkId = workId,
                Type = type,
                AtUtc = now,
                ByUserId = byUserId,
                Data = data,

                IsDeleted = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = byUserId,
                UpdatedByUserId = byUserId
            };

            await _ctx.WorkHistories.InsertOneAsync(doc, cancellationToken: ct);
        }
    }
}
