using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignmentReports.Runtime;

/// <summary>
/// Advances the Work-level source fence in the same Mongo transaction as a
/// membership-affecting runtime mutation. Call once per transaction with the
/// complete set of affected Work ids; duplicate ids are intentionally folded.
/// </summary>
public static class WorkDirectSourceRevisionFence
{
    public static Task IncrementAsync(
        MongoDbContext context,
        IClientSessionHandle session,
        string workId,
        CancellationToken ct)
        => IncrementAsync(context, session, new[] { workId }, ct);

    public static async Task IncrementAsync(
        MongoDbContext context,
        IClientSessionHandle session,
        IEnumerable<string?> workIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(workIds);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "P9_DIRECT_SOURCE_FENCE_TRANSACTION_REQUIRED");
        }

        var distinctWorkIds = workIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctWorkIds.Length == 0)
        {
            throw new InvalidOperationException(
                "P9_DIRECT_SOURCE_FENCE_WORK_REQUIRED");
        }

        var result = await context.Works.UpdateManyAsync(
            session,
            work =>
                distinctWorkIds.Contains(work.Id) &&
                !work.IsDeleted,
            Builders<Work>.Update.Inc(
                work => work.DirectSourceRevision,
                1L),
            cancellationToken: ct);
        if (result.MatchedCount != distinctWorkIds.Length ||
            result.ModifiedCount != distinctWorkIds.Length)
        {
            throw new InvalidOperationException(
                "P9_DIRECT_SOURCE_FENCE_WORK_NOT_FOUND");
        }
    }
}
