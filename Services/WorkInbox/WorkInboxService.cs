using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.Notifications;
using tdtd_be.DTOs.WorkInbox;
using tdtd_be.Services.Notifications;
using tdtd_be.Services.Works;

namespace tdtd_be.Services.WorkInbox;

public sealed class WorkInboxService(MongoDbContext ctx, INotificationService notifications,
    IWorkPermissionService workPermission, IConfiguration configuration)
{
    public double? DueSoonHours => configuration.GetValue<double?>("WorkInbox:DueSoonHours") is > 0 and <= 8760
        ? configuration.GetValue<double?>("WorkInbox:DueSoonHours") : 24;
    public bool NotifyReviewRequired => configuration.GetValue<bool>("WorkInbox:NotifyReviewRequired");

    private List<BsonDocument> Pipeline(string actor, bool closed = false) => WorkInboxPipeline.Build(
        ObjectId.Parse(actor), ctx.WorkAssignments.CollectionNamespace.CollectionName, ctx.Works.CollectionNamespace.CollectionName,
        ctx.WorkReportPeriods.CollectionNamespace.CollectionName, ctx.DynamicFormCloneRequests.CollectionNamespace.CollectionName,
        ctx.WorkAssignmentReports.CollectionNamespace.CollectionName, ctx.WorkAssignmentReportLogs.CollectionNamespace.CollectionName,
        DateTime.UtcNow, DueSoonHours, closed);

    private async Task<List<BsonDocument>> Read(List<BsonDocument> pipeline, CancellationToken ct)
        => await ctx.WorkReportPeriods.Aggregate<BsonDocument>(pipeline,
            new AggregateOptions { AllowDiskUse = true, MaxTime = TimeSpan.FromSeconds(20) }).ToListAsync(ct);

    public async Task<WorkInboxSummary> SummaryAsync(string actor, CancellationToken ct)
    {
        var pipeline = Pipeline(actor);
        pipeline.Add(new BsonDocument("$group", new BsonDocument {
            { "_id", "$function" }, { "count", new BsonDocument("$sum", 1) } }));
        var rows = await Read(pipeline, ct);
        var recent = await RecentAsync(actor, ct);
        return new WorkInboxSummary {
            Total = rows.Sum(x => x["count"].ToInt64()),
            ByFunction = rows.ToDictionary(x => x["_id"].AsString, x => x["count"].ToInt64()),
            RecentUnreadCount = recent.RecentUnreadCount, DueSoonHours = DueSoonHours,
            NotifyReviewRequired = NotifyReviewRequired
        };
    }

    public async Task<WorkInboxPage> SearchAsync(WorkInboxSearchRequest request, string actor, CancellationToken ct)
    {
        var pipeline = Pipeline(actor);
        AddFilters(pipeline, request);
        if (request.CursorId is not null)
        {
            var rank = request.CursorPriority!.Value;
            var due = new BsonDateTime(request.CursorDueAtUtc!.Value);
            pipeline.Add(WorkInboxPipeline.Match(new BsonDocument("$or", new BsonArray {
                new BsonDocument("priority", new BsonDocument("$gt", rank)),
                new BsonDocument { { "priority", rank }, { "sortDueAtUtc", new BsonDocument("$gt", due) } },
                new BsonDocument { { "priority", rank }, { "sortDueAtUtc", due }, { "id", new BsonDocument("$gt", request.CursorId) } } })));
        }
        var size = Math.Clamp(request.PageSize, 1, 50);
        pipeline.Add(WorkInboxPipeline.Sort());
        pipeline.Add(new BsonDocument("$limit", size + 1));
        var rows = await Read(pipeline, ct);
        var items = rows.Take(size).Select(Map).ToList();
        var more = rows.Count > size;
        var last = items.LastOrDefault();
        return new WorkInboxPage { Items = items, HasMore = more,
            NextCursorId = more ? last?.Id : null, NextCursorPriority = more ? last?.Priority : null,
            NextCursorDueAtUtc = more ? last?.SortDueAtUtc : null };
    }

    public async Task<object> GroupsAsync(WorkInboxSearchRequest request, string actor, CancellationToken ct)
    {
        var pipeline = Pipeline(actor);
        AddFilters(pipeline, request);
        pipeline.Add(new BsonDocument("$group", new BsonDocument { { "_id", "$workId" },
            { "workName", new BsonDocument("$first", "$workName") }, { "count", new BsonDocument("$sum", 1) },
            { "priority", new BsonDocument("$min", "$priority") }, { "sortDueAtUtc", new BsonDocument("$min", "$sortDueAtUtc") } }));
        if (request.CursorId is not null) pipeline.Add(WorkInboxPipeline.Match(new BsonDocument("_id", new BsonDocument("$gt", request.CursorId))));
        pipeline.Add(new BsonDocument("$sort", new BsonDocument("_id", 1)));
        var size = Math.Clamp(request.PageSize, 1, 50);
        pipeline.Add(new BsonDocument("$limit", size + 1));
        var rows = await Read(pipeline, ct);
        return new { Items = rows.Take(size).Select(x => new { WorkId = Text(x, "_id"), WorkName = Text(x, "workName"),
            Total = x["count"].ToInt64() }), HasMore = rows.Count > size,
            NextCursorId = rows.Count > size ? Text(rows[size - 1], "_id") : null };
    }

    private static void AddFilters(List<BsonDocument> pipeline, WorkInboxSearchRequest request)
    {
        var filter = new BsonDocument();
        if (!string.IsNullOrWhiteSpace(request.WorkId)) filter["workId"] = request.WorkId;
        if (!string.IsNullOrWhiteSpace(request.AssignmentId)) filter["assignmentId"] = request.AssignmentId;
        if (!string.IsNullOrWhiteSpace(request.Function)) filter["function"] = request.Function;
        if (filter.ElementCount > 0) pipeline.Add(WorkInboxPipeline.Match(filter));
    }

    public async Task<WorkInboxItem?> ItemAsync(string id, string actor, CancellationToken ct)
    {
        var parts = id.Split(':', 2);
        if (parts.Length != 2 || !ObjectId.TryParse(parts[1], out _)) return null;
        if (parts[0] == "work")
        {
            await workPermission.EnsureCanReadAsync(parts[1], actor, ct);
            var work = await ctx.Works.Find(x => x.Id == parts[1] && !x.IsDeleted).FirstOrDefaultAsync(ct);
            return work is null ? null : new WorkInboxItem { Id = id, Function = "ASSIGNMENT", WorkId = work.Id,
                WorkName = work.Name, Title = work.Name, State = "INFORMATION", Target = new WorkInboxTarget { Kind = "WORK", WorkId = work.Id } };
        }
        var pipeline = Pipeline(actor, closed: true);
        pipeline.Add(WorkInboxPipeline.Match(new BsonDocument("id", id)));
        pipeline.Add(new BsonDocument("$limit", 1));
        return (await Read(pipeline, ct)).Select(Map).FirstOrDefault();
    }

    public async Task<WorkInboxNotificationPage> RecentAsync(string actor, CancellationToken ct)
    {
        var types = new List<string> { "WORK_DUE", "ASSIGNMENT_DUE", "REPORT_DUE", "WORK_DUE_SOON", "ASSIGNMENT_DUE_SOON",
            "REPORT_DUE_SOON", "REPORT_APPROVED", "DYNAMIC_FORM_CLONE_APPROVED", "ASSIGNMENT_HANDOVER_APPROVED", "ASSIGNMENT_ASSIGNED", "ASSIGNMENT_HANDOVER_RECEIVED" };
        if (NotifyReviewRequired) types.Add("REPORT_REVIEW_REQUIRED");
        return NotificationPage(await notifications.SearchAsync(new NotificationSearchRequest { PageSize = 20, Types = types }, actor, ct));
    }

    public async Task<WorkInboxNotificationPage> HistoryAsync(NotificationSearchRequest request, string actor, CancellationToken ct)
        => NotificationPage(await notifications.SearchAsync(request, actor, ct));

    private static WorkInboxNotificationPage NotificationPage(NotificationSearchResponse page) => new() {
        Items = page.Items.Select(row => new WorkInboxNotification { Notification = row, ItemId = NotificationItemId(row) }).ToList(),
        HasMore = page.HasMore, NextCursorOccurredAtUtc = page.NextCursorOccurredAtUtc, NextCursorId = page.NextCursorId,
        RecentUnreadCount = page.Items.Count(x => x.ReadAtUtc is null)
    };

    public static string? NotificationItemId(NotificationRowDto row)
    {
        if (!string.IsNullOrWhiteSpace(row.RequestId) && row.Type.StartsWith("DYNAMIC_FORM_CLONE", StringComparison.Ordinal)) return "clone:" + row.RequestId;
        if (!string.IsNullOrWhiteSpace(row.WorkReportPeriodId)) return (row.Type == "REPORT_REVIEW_REQUIRED" ? "review:" : "report:") + row.WorkReportPeriodId;
        if (!string.IsNullOrWhiteSpace(row.WorkAssignmentId)) return "assignment:" + row.WorkAssignmentId;
        return string.IsNullOrWhiteSpace(row.WorkId) ? null : "work:" + row.WorkId;
    }

    private static string? Text(BsonDocument row, string field) => row.TryGetValue(field, out var value) && !value.IsBsonNull ? value.AsString : null;
    private static DateTime? Date(BsonDocument row, string field) => row.TryGetValue(field, out var value) && value.IsBsonDateTime ? value.ToUniversalTime() : null;
    private static WorkInboxItem Map(BsonDocument row)
    {
        var actionable = row.GetValue("requiresAction", false).AsBoolean;
        var title = Text(row, "title");
        if (string.IsNullOrWhiteSpace(title)) title = Text(row, "assignmentName");
        if (string.IsNullOrWhiteSpace(title)) title = "Công việc cần thực hiện";
        return new WorkInboxItem {
            Id = Text(row, "id")!, Function = Text(row, "function")!, Title = title,
            Description = Text(row, "description"), CanRequestClone = row.GetValue("canRequestClone", false).AsBoolean,
            WorkId = Text(row, "workId")!, WorkName = Text(row, "workName") ?? "Công việc",
            AssignmentId = Text(row, "assignmentId"), AssignmentCode = Text(row, "assignmentCode"), AssignmentName = Text(row, "assignmentName"),
            PeriodKey = Text(row, "periodKey"), AssigneeName = Text(row, "assigneeName"), DueAtUtc = Date(row, "dueAtUtc"),
            State = Text(row, "state") ?? "OPEN", RequiresAction = actionable, IsResubmission = row.GetValue("isResubmission", false).AsBoolean,
            Priority = row.GetValue("priority", 2).ToInt32(), SortDueAtUtc = Date(row, "sortDueAtUtc") ?? DateTime.MaxValue,
            Target = new WorkInboxTarget { Kind = Text(row, "targetKind")!, WorkId = Text(row, "workId")!, AssignmentId = Text(row, "assignmentId"),
                PeriodId = Text(row, "periodId"), ReportId = Text(row, "reportId"), AssigneeUserId = Text(row, "assigneeUserId"),
                RequestId = Text(row, "requestId"), ReadOnly = !actionable }
        };
    }
}
