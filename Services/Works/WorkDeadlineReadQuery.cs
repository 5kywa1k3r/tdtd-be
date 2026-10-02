using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;

namespace tdtd_be.Services.Works;

internal static class WorkDeadlineReadQuery
{
    // Resolve from Work at read time, including existing projections created before
    // deadline fallback was introduced. Do not rewrite stored source dates.
    internal static IAggregateFluent<WorkListDocRole> WithDeadline(
        IAggregateFluent<WorkListDocRole> query, string worksCollection)
        => query.AppendStage<WorkListDocRole>(new BsonDocument("$lookup", new BsonDocument
        {
            { "from", worksCollection }, { "localField", "workId" }, { "foreignField", "_id" },
            { "pipeline", new BsonArray { new BsonDocument("$project", new BsonDocument
                { { "dueDate", 1 }, { "endDate", 1 }, { "isDeleted", 1 } }) } },
            { "as", "deadlineWork" }
        }))
        .AppendStage<WorkListDocRole>(new BsonDocument("$unwind", "$deadlineWork"))
        .Match(new BsonDocument("deadlineWork.isDeleted", false))
        .AppendStage<WorkListDocRole>(new BsonDocument("$set", new BsonDocument("dueDate",
            new BsonDocument("$ifNull", new BsonArray { "$deadlineWork.dueDate", "$deadlineWork.endDate" }))))
        .AppendStage<WorkListDocRole>(new BsonDocument("$unset", "deadlineWork"));

    internal static FilterDefinition<Work> Overlaps(DateTime fromDate, DateTime toDate, DateTime fromUtc, DateTime toUtc)
    {
        var f = Builders<Work>.Filter;
        var effectiveEndAfter = f.Gte(x => x.DueDate, fromDate) |
            (f.Eq(x => x.DueDate, null) & (f.Eq(x => x.EndDate, null) | f.Gte(x => x.EndDate, fromDate)));
        var effectiveDueInRange = (f.Gte(x => x.DueDate, fromDate) & f.Lte(x => x.DueDate, toDate)) |
            (f.Eq(x => x.DueDate, null) & f.Gte(x => x.EndDate, fromDate) & f.Lte(x => x.EndDate, toDate));
        return (f.Ne(x => x.StartDate, null) & f.Lte(x => x.StartDate, toDate) & effectiveEndAfter) |
            effectiveDueInRange |
            (f.Eq(x => x.StartDate, null) & f.Eq(x => x.EndDate, null) & f.Eq(x => x.DueDate, null) &
             f.Gte(x => x.UpdatedAtUtc, fromUtc) & f.Lte(x => x.UpdatedAtUtc, toUtc));
    }
}
