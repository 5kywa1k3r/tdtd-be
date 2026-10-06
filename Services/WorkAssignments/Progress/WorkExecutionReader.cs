using System.Linq.Expressions;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Progress;

public static class WorkExecutionReader
{
    public static async Task<WorkExecutionFacts> ReadAsync(MongoDbContext db, WorkAssignment a, Work w,
        DateTime now, CancellationToken ct, IClientSessionHandle? session = null)
    {
        Task<List<T>> Read<T>(IMongoCollection<T> collection, Expression<Func<T, bool>> filter) =>
            (session == null ? collection.Find(filter) : collection.Find(session, filter)).ToListAsync(ct);
        var parent = string.IsNullOrWhiteSpace(a.ParentAssignmentId) ? null :
            (await Read(db.WorkAssignments, x => x.Id == a.ParentAssignmentId && !x.IsDeleted)).FirstOrDefault();
        var bindings = await Read(db.WorkTemplateAssignees, x => x.WorkAssignmentId == a.Id && !x.IsDeleted);
        var periods = await Read(db.WorkReportPeriods, x => x.WorkAssignmentId == a.Id && !x.IsDeleted && x.IsActive);
        var reports = await Read(db.WorkAssignmentReports, x => x.WorkAssignmentId == a.Id && !x.IsDeleted);
        return WorkExecutionProgressPolicy.Evaluate(a, w, parent, bindings, periods, reports, now);
    }
}
