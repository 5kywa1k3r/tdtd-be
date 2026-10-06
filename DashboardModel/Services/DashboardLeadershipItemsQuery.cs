using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.DashboardModel.DTOs;
using static tdtd_be.DashboardModel.Services.DashboardLeadershipDbProjection;

namespace tdtd_be.DashboardModel.Services;

public sealed partial class DashboardOverviewService
{
    private static BsonDocument LeadershipAnd(params BsonDocument[] filters) => new("$and", new BsonArray(filters.Where(x => x.ElementCount > 0)));
    private static BsonDocument LeadershipActive() => new() { { "isDeleted", false }, { "isActive", true } };
    private static BsonArray LeadershipIds(IEnumerable<string> ids) => new(ids.Select(id => (BsonValue)ObjectId.Parse(id)));
    private BsonDocument LeadershipLookup(string collection, BsonDocument variables, IEnumerable<BsonDocument> pipeline, string name)
        => new("$lookup", new BsonDocument { { "from", collection }, { "let", variables }, { "pipeline", new BsonArray(pipeline) }, { "as", name } });

    private List<BsonDocument> LeadershipWorkReadStages(LeadershipQueryAccess access)
    {
        var pipeline = new List<BsonDocument> { new("$match", new BsonDocument { { "isDeleted", false }, { "type", new BsonDocument("$in", new BsonArray { 1, 2 }) } }) };
        if (!access.Global)
        {
            pipeline.Add(LeadershipLookup(_ctx.WorkAssignments.CollectionNamespace.CollectionName, new("work", "$_id"), new[] {
                new BsonDocument("$match", LeadershipAnd(LeadershipActive(), access.Assignments, new("$expr", E("$eq", "$workId", "$$work")))),
                new BsonDocument("$limit", 1), new BsonDocument("$project", new BsonDocument("_id", 1)) }, "__access"));
            pipeline.Add(new("$match", new BsonDocument("$or", new BsonArray { new BsonDocument("_id", new BsonDocument("$in", LeadershipIds(access.WorkIds))), new BsonDocument("__access.0", new BsonDocument("$exists", true)) })));
        }
        return pipeline;
    }

    private BsonDocument LeadershipWorkJoin(string workId = "$workId") => LeadershipLookup(_ctx.Works.CollectionNamespace.CollectionName, new("work", workId), new[] {
        new BsonDocument("$match", new BsonDocument { { "isDeleted", false }, { "type", new BsonDocument("$in", new BsonArray { 1, 2 }) }, { "$expr", E("$eq", "$_id", "$$work") } }),
        new BsonDocument("$project", new BsonDocument { { "name", 1 }, { "code", 1 }, { "autoCode", 1 }, { "type", 1 }, { "status", 1 }, { "priority", 1 }, { "startDate", 1 }, { "dueDate", 1 }, { "endDate", 1 }, { "completedDate", 1 }, { "createdAtUtc", 1 } }) }, "__work");

    private BsonDocument LeadershipParentJoin(LeadershipQueryAccess access) => LeadershipLookup(_ctx.WorkAssignments.CollectionNamespace.CollectionName,
        new BsonDocument { { "parent", new BsonDocument("$convert", new BsonDocument { { "input", "$parentAssignmentId" }, { "to", "objectId" }, { "onError", BsonNull.Value }, { "onNull", BsonNull.Value } }) }, { "work", "$workId" } }, new[] {
            new BsonDocument("$match", LeadershipAnd(LeadershipActive(), access.Assignments, new("$expr", E("$and", E("$eq", "$_id", "$$parent"), E("$eq", "$workId", "$$work"))))),
            new BsonDocument("$limit", 1), new BsonDocument("$project", new BsonDocument("_id", 1)) }, "__parent");

    private async Task<DashboardLeadershipItemsDto> QueryLeadershipItemsAsync(DashboardLeadershipItemsRequest req, string selection,
        DateTime from, DateTime to, DateTime now, CancellationToken ct)
    {
        var access = await LoadLeadershipQueryAccessAsync(ct);
        var options = new AggregateOptions { AllowDiskUse = true, MaxTime = TimeSpan.FromSeconds(30) };
        if (req.WorkId is not null)
        {
            if (!ObjectId.TryParse(req.WorkId, out var requestedId)) throw DashboardLeadershipProjection.Invalid("workId", req.WorkId);
            var check = new List<BsonDocument> { new("$match", new BsonDocument("_id", requestedId)) };
            check.AddRange(LeadershipWorkReadStages(access)); check.Add(new("$limit", 1)); check.Add(new("$project", new BsonDocument("_id", 1)));
            if (await _ctx.Works.Aggregate<BsonDocument>(check, options).FirstOrDefaultAsync(ct) is null)
                throw AppExceptionFactory.Forbidden(AppErrorCode.DASHBOARD_WORK_READ_FORBIDDEN, new { req.WorkId });
        }
        var kind = selection.StartsWith("REPORT_") ? "REPORT" : selection.StartsWith("ASSIGNMENT_") || selection == "DIRECT_ASSIGNMENTS" ? "ASSIGNMENT" : "WORK";
        var pipeline = new List<BsonDocument>();
        if (kind == "WORK")
        {
            var early = WorkPrefilter(selection, DashboardLeadershipProjection.BusinessToday(now));
            if (req.WorkId is not null) early["_id"] = ObjectId.Parse(req.WorkId);
            if (req.Status.HasValue) early["status"] = req.Status.Value;
            if (req.Priority.HasValue) early["priority"] = req.Priority.Value;
            if (early.ElementCount > 0) pipeline.Add(new("$match", early));
            pipeline.AddRange(LeadershipWorkReadStages(access));
        }
        else if (kind == "ASSIGNMENT")
        {
            var early = new BsonDocument();
            if (req.WorkId is not null) early["workId"] = ObjectId.Parse(req.WorkId);
            if (req.Status.HasValue) early["progressStatus"] = req.Status.Value;
            pipeline.Add(new("$match", LeadershipAnd(LeadershipActive(), access.Assignments, early)));
            if (selection == "DIRECT_ASSIGNMENTS") { pipeline.Add(LeadershipParentJoin(access)); pipeline.Add(new("$match", new BsonDocument("__parent.0", new BsonDocument("$exists", false)))); }
            pipeline.Add(LeadershipWorkJoin()); pipeline.Add(new("$unwind", "$__work"));
        }
        else
        {
            var periodFilter = LeadershipActive();
            periodFilter["dueAtUtc"] = new BsonDocument { { "$gte", new BsonDateTime(DateTime.SpecifyKind(from.AddHours(-7), DateTimeKind.Utc)) }, { "$lt", new BsonDateTime(DateTime.SpecifyKind(to.AddDays(1).AddHours(-7), DateTimeKind.Utc)) } };
            if (req.WorkId is not null) periodFilter["workId"] = ObjectId.Parse(req.WorkId);
            pipeline.Add(new("$match", periodFilter));
            var assignmentPipeline = new List<BsonDocument> {
                new("$match", LeadershipAnd(LeadershipActive(), access.Assignments, new("$expr", E("$and", E("$eq", "$_id", "$$assignment"), E("$eq", "$workId", "$$work"), E("$in", "$$user", N("$assignees.userId", new BsonArray())))))) };
            // A unit-head's recipient-only node cannot expose another unit's reports.
            if (access.UnitIds.Count > 0) assignmentPipeline.Add(new("$match", new BsonDocument("$or", new BsonArray {
                access.FullAssignments, new BsonDocument("$expr", new BsonDocument("$anyElementTrue", new BsonArray { new BsonDocument("$map", new BsonDocument {
                    { "input", N("$assignees", new BsonArray()) }, { "as", "u" }, { "in", E("$and", E("$eq", "$$u.userId", "$$user"), E("$in", "$$u.unitId", LeadershipIds(access.UnitIds))) } }) })) })));
            assignmentPipeline.Add(new("$project", new BsonDocument { { "name", 1 }, { "code", 1 }, { "dynamicFormTemplateName", 1 }, { "dynamicExcelName", 1 }, { "progressStatus", 1 }, { "assignees", 1 } }));
            pipeline.Add(LeadershipLookup(_ctx.WorkAssignments.CollectionNamespace.CollectionName, new BsonDocument { { "assignment", "$workAssignmentId" }, { "work", "$workId" }, { "user", "$assigneeUserId" } }, assignmentPipeline, "__assignment"));
            pipeline.Add(new("$unwind", "$__assignment"));
            pipeline.Add(LeadershipWorkJoin()); pipeline.Add(new("$unwind", "$__work"));
            pipeline.Add(LeadershipLookup(_ctx.WorkAssignmentReports.CollectionNamespace.CollectionName,
                new BsonDocument { { "period", "$_id" }, { "work", "$workId" }, { "assignment", "$workAssignmentId" }, { "user", "$assigneeUserId" } }, new[] {
                    new BsonDocument("$match", new BsonDocument { { "isDeleted", false }, { "isActive", true }, { "isCurrent", true }, { "$expr", E("$and", E("$eq", "$workReportPeriodId", "$$period"), E("$eq", "$workId", "$$work"), E("$eq", "$workAssignmentId", "$$assignment"), E("$eq", "$assigneeUserId", "$$user")) } }),
                    // Two current candidates are enough to reject an ambiguous pointer; never read payload.
                    new BsonDocument("$limit", 2), new BsonDocument("$project", new BsonDocument { { "status", 1 }, { "dueAtUtc", 1 }, { "submittedAtUtc", 1 }, { "returnedAtUtc", 1 }, { "isLateSubmission", 1 } }) }, "__candidates"));
            pipeline.Add(new("$set", new BsonDocument("__report", If(E("$and", E("$eq", new BsonDocument("$size", "$__candidates"), 1), E("$eq", new BsonDocument("$arrayElemAt", new BsonArray { "$__candidates._id", 0 }), "$currentReportId")), new BsonDocument("$arrayElemAt", new BsonArray { "$__candidates", 0 }), BsonNull.Value))));
        }
        pipeline.Add(new("$project", Item(kind, now)));
        pipeline.Add(new("$match", Selection(req, selection, from, to, DashboardLeadershipProjection.BusinessToday(now))));
        pipeline.Add(Page(req.Page, req.PageSize));
        var result = kind == "WORK" ? await _ctx.Works.Aggregate<BsonDocument>(pipeline, options).SingleAsync(ct)
            : kind == "ASSIGNMENT" ? await _ctx.WorkAssignments.Aggregate<BsonDocument>(pipeline, options).SingleAsync(ct)
            : await _ctx.WorkReportPeriods.Aggregate<BsonDocument>(pipeline, options).SingleAsync(ct);
        var jsonOptions = new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson };
        var rows = result["Rows"].AsBsonArray.Select(row => JsonSerializer.Deserialize<DashboardLeadershipItemDto>(row.ToJson(jsonOptions))!).ToList();
        // Enrich only the requested page. Counts stay in Mongo and retain visible-entry semantics.
        if (rows.Count > 0)
        {
            var countPipeline = new List<BsonDocument> { new("$match", LeadershipAnd(LeadershipActive(), access.Assignments, new("workId", new BsonDocument("$in", LeadershipIds(rows.Select(x => x.WorkId).Distinct()))))),
                LeadershipParentJoin(access), new("$match", new BsonDocument("__parent.0", new BsonDocument("$exists", false))),
                new("$group", new BsonDocument { { "_id", "$workId" }, { "count", new BsonDocument("$sum", 1) }, { "completed", new BsonDocument("$sum", If(E("$eq", "$progressStatus", 2), 1, 0)) } }) };
            var counts = await _ctx.WorkAssignments.Aggregate<BsonDocument>(countPipeline, options).ToListAsync(ct);
            foreach (var row in rows) { var count = counts.FirstOrDefault(x => x["_id"].ToString() == row.WorkId); if (count is not null) { row.AssignmentCount = count["count"].ToInt32(); row.CompletedAssignmentCount = count["completed"].ToInt32(); } }
        }
        return new() { Rows = rows, TotalRows = result["Total"].AsBsonArray.FirstOrDefault()?.AsBsonDocument["count"].ToInt32() ?? 0,
            Page = req.Page, PageSize = req.PageSize, Selection = selection, ScopeLabel = access.Label, GeneratedAtUtc = now };
    }
}
