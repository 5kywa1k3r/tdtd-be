using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;
using tdtd_be.Data;
using tdtd_be.DTOs.Notifications;
using tdtd_be.DTOs.WorkInbox;
using tdtd_be.Services.Notifications;
using tdtd_be.Services.Works;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;

namespace tdtd_be.Services.WorkInbox;

public sealed class WorkInboxService(MongoDbContext ctx, INotificationService notifications,
    IWorkPermissionService workPermission, IConfiguration configuration)
{
    public double? DueSoonHours => configuration.GetValue<double?>("WorkInbox:DueSoonHours") is > 0 and <= 8760
        ? configuration.GetValue<double?>("WorkInbox:DueSoonHours") : 24;
    public bool NotifyReviewRequired => configuration.GetValue<bool>("WorkInbox:NotifyReviewRequired");

    private List<BsonDocument> Pipeline(string actor, bool closed = false, IReadOnlyCollection<string>? itemIds = null,
        IReadOnlyCollection<string>? assignmentIds = null) => WorkInboxPipeline.Build(
        ObjectId.Parse(actor), ctx.WorkAssignments.CollectionNamespace.CollectionName, ctx.Works.CollectionNamespace.CollectionName,
        ctx.WorkReportPeriods.CollectionNamespace.CollectionName, ctx.DynamicFormCloneRequests.CollectionNamespace.CollectionName,
        ctx.WorkAssignmentReports.CollectionNamespace.CollectionName, ctx.WorkAssignmentReportLogs.CollectionNamespace.CollectionName,
        ctx.DynamicFormTemplates.CollectionNamespace.CollectionName, DateTime.UtcNow, DueSoonHours, closed, itemIds, assignmentIds);

    private async Task<List<BsonDocument>> Read(List<BsonDocument> pipeline, CancellationToken ct)
        => await ctx.WorkReportPeriods.Aggregate<BsonDocument>(pipeline,
            new AggregateOptions { AllowDiskUse = true, MaxTime = TimeSpan.FromSeconds(20) }).ToListAsync(ct);

    public async Task<WorkInboxSummary> SummaryAsync(string actor, CancellationToken ct, string? workId = null,
        string? searchText = null, string? state = null, DateTime? dueFromUtc = null, DateTime? dueBeforeUtc = null,
        bool? handled = false, string? assignmentId = null)
    {
        var pipeline = Pipeline(actor, closed: handled != false);
        AddFilters(pipeline, new WorkInboxSearchRequest { WorkId = workId, AssignmentId = assignmentId, SearchText = searchText, State = state,
            DueFromUtc = dueFromUtc, DueBeforeUtc = dueBeforeUtc, Handled = handled });
        pipeline.Add(new BsonDocument("$group", new BsonDocument {
            { "_id", "$function" }, { "count", new BsonDocument("$sum", 1) } }));
        var rows = await Read(pipeline, ct);
        // Summary needs only the bounded recent unread count; skip subject/source enrichment here.
        var recent = await notifications.SearchAsync(new NotificationSearchRequest { PageSize = 20, Types = RecentTypes() }, actor, ct);
        return new WorkInboxSummary {
            Total = rows.Sum(x => x["count"].ToInt64()),
            ByFunction = rows.ToDictionary(x => x["_id"].AsString, x => x["count"].ToInt64()),
            RecentUnreadCount = recent.Items.Count(x => x.ReadAtUtc is null), DueSoonHours = DueSoonHours,
            NotifyReviewRequired = NotifyReviewRequired
        };
    }

    public async Task<WorkInboxPage> SearchAsync(WorkInboxSearchRequest request, string actor, CancellationToken ct)
    {
        var pipeline = Pipeline(actor, closed: request.Handled != false);
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
        if (request.PageNumber.HasValue) pipeline.Add(new BsonDocument("$skip", checked((request.PageNumber.Value - 1) * size)));
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
        var pipeline = Pipeline(actor, closed: request.Handled != false);
        AddFilters(pipeline, request);
        pipeline.Add(new BsonDocument("$group", new BsonDocument { { "_id", "$workId" },
            { "workName", new BsonDocument("$first", "$workName") }, { "count", new BsonDocument("$sum", 1) },
            { "priority", new BsonDocument("$min", "$priority") }, { "sortDueAtUtc", new BsonDocument("$min", "$sortDueAtUtc") } }));
        if (request.CursorId is not null) pipeline.Add(WorkInboxPipeline.Match(new BsonDocument("_id", new BsonDocument("$gt", request.CursorId))));
        pipeline.Add(new BsonDocument("$sort", new BsonDocument("_id", 1)));
        var size = Math.Clamp(request.PageSize, 1, 50);
        if (request.PageNumber.HasValue) pipeline.Add(new BsonDocument("$skip", checked((request.PageNumber.Value - 1) * size)));
        pipeline.Add(new BsonDocument("$limit", size + 1));
        var rows = await Read(pipeline, ct);
        return new { Items = rows.Take(size).Select(x => new { WorkId = Text(x, "_id"), WorkName = Text(x, "workName"),
            Total = x["count"].ToInt64() }), HasMore = rows.Count > size,
            NextCursorId = rows.Count > size ? Text(rows[size - 1], "_id") : null };
    }

    public async Task<WorkInboxAssignmentGroupPage> AssignmentGroupsAsync(WorkInboxSearchRequest request, string actor, CancellationToken ct)
    {
        var pipeline = Pipeline(actor, closed: request.Handled != false);
        AddFilters(pipeline, request);
        var noDue = new BsonDateTime(DateTime.MaxValue);
        BsonDocument Eq(string field, BsonValue value) => new("$eq", new BsonArray { field, value });
        BsonDocument Count(BsonValue condition) => new("$sum", new BsonDocument("$cond", new BsonArray { condition, 1, 0 }));
        BsonDocument Urgency(int priority) => new("$and", new BsonArray { Eq("$processingState", "PENDING"), Eq("$priority", priority) });
        // Group the complete authorized, filtered source set before paging cards. PageSize is a
        // number of assignments; it never caps member counts or selects one arbitrary recipient.
        pipeline.Add(new BsonDocument("$group", new BsonDocument {
            { "_id", "$assignmentId" }, { "assignmentName", new BsonDocument("$first", "$assignmentName") },
            { "workId", new BsonDocument("$first", "$workId") }, { "workName", new BsonDocument("$first", "$workName") },
            { "function", new BsonDocument("$first", "$function") }, { "total", new BsonDocument("$sum", 1) },
            { "pendingCount", Count(Eq("$processingState", "PENDING")) }, { "handledCount", Count(Eq("$processingState", "HANDLED")) },
            { "overdueCount", Count(Urgency(0)) }, { "dueSoonCount", Count(Urgency(1)) },
            { "resubmissionCount", Count(Eq("$isResubmission", true)) },
            { "assignees", new BsonDocument("$addToSet", "$assigneeUserId") }, { "periods", new BsonDocument("$addToSet", "$periodKey") },
            { "priority", new BsonDocument("$min", "$priority") },
            { "memberDue", new BsonDocument("$min", new BsonDocument("$ifNull", new BsonArray { "$dueAtUtc", noDue })) },
            { "pendingDue", new BsonDocument("$min", new BsonDocument("$cond", new BsonArray {
                Eq("$processingState", "PENDING"), new BsonDocument("$ifNull", new BsonArray { "$dueAtUtc", noDue }), noDue })) }
        }));
        BsonDocument DistinctCount(string field) => new("$size", new BsonDocument("$filter", new BsonDocument {
            { "input", field }, { "as", "value" }, { "cond", new BsonDocument("$and", new BsonArray {
                new BsonDocument("$ne", new BsonArray { "$$value", BsonNull.Value }),
                new BsonDocument("$ne", new BsonArray { "$$value", "" }) }) } }));
        pipeline.Add(new BsonDocument("$set", new BsonDocument {
            { "sortDueAtUtc", new BsonDocument("$cond", new BsonArray { Eq("$pendingDue", noDue), "$memberDue", "$pendingDue" }) },
            { "assigneeCount", DistinctCount("$assignees") }, { "periodCount", DistinctCount("$periods") }
        }));
        pipeline.Add(new BsonDocument("$set", new BsonDocument("dueAtUtc", new BsonDocument("$cond", new BsonArray {
            Eq("$sortDueAtUtc", noDue), BsonNull.Value, "$sortDueAtUtc" }))));
        if (request.CursorId is not null) {
            var rank = request.CursorPriority!.Value;
            var due = new BsonDateTime(request.CursorDueAtUtc!.Value);
            pipeline.Add(WorkInboxPipeline.Match(new BsonDocument("$or", new BsonArray {
                new BsonDocument("priority", new BsonDocument("$gt", rank)),
                new BsonDocument { { "priority", rank }, { "sortDueAtUtc", new BsonDocument("$gt", due) } },
                new BsonDocument { { "priority", rank }, { "sortDueAtUtc", due }, { "_id", new BsonDocument("$gt", request.CursorId) } } })));
        }
        pipeline.Add(new BsonDocument("$sort", new BsonDocument { { "priority", 1 }, { "sortDueAtUtc", 1 }, { "_id", 1 } }));
        var size = Math.Clamp(request.PageSize, 1, 50);
        if (request.PageNumber.HasValue) pipeline.Add(new BsonDocument("$skip", checked((request.PageNumber.Value - 1) * size)));
        pipeline.Add(new BsonDocument("$limit", size + 1));
        // Remove internal distinct sets; response has aggregate facts, never member item/target arrays.
        pipeline.Add(new BsonDocument("$project", new BsonDocument { { "assignees", 0 }, { "periods", 0 }, { "pendingDue", 0 }, { "memberDue", 0 } }));
        var rows = await Read(pipeline, ct);
        var items = rows.Take(size).Select(row => new WorkInboxAssignmentGroup {
            AssignmentId = Text(row, "_id")!, AssignmentName = Text(row, "assignmentName") ?? Text(row, "workName") ?? "Công việc",
            WorkId = Text(row, "workId")!, WorkName = Text(row, "workName") ?? "Công việc", Function = Text(row, "function")!,
            Total = row["total"].ToInt64(), PendingCount = row["pendingCount"].ToInt64(), HandledCount = row["handledCount"].ToInt64(),
            OverdueCount = row["overdueCount"].ToInt64(), DueSoonCount = row["dueSoonCount"].ToInt64(), ResubmissionCount = row["resubmissionCount"].ToInt64(),
            AssigneeCount = row["assigneeCount"].ToInt64(), PeriodCount = row["periodCount"].ToInt64(),
            DueAtUtc = Date(row, "dueAtUtc"), Priority = row["priority"].ToInt32(), SortDueAtUtc = Date(row, "sortDueAtUtc") ?? DateTime.MaxValue
        }).ToList();
        var more = rows.Count > size;
        var last = items.LastOrDefault();
        return new WorkInboxAssignmentGroupPage { Items = items, HasMore = more,
            NextCursorId = more ? last?.AssignmentId : null, NextCursorPriority = more ? last?.Priority : null,
            NextCursorDueAtUtc = more ? last?.SortDueAtUtc : null };
    }

    private static void AddFilters(List<BsonDocument> pipeline, WorkInboxSearchRequest request)
    {
        var filter = new BsonDocument();
        if (!string.IsNullOrWhiteSpace(request.WorkId)) filter["workId"] = request.WorkId;
        if (!string.IsNullOrWhiteSpace(request.AssignmentId)) filter["assignmentId"] = request.AssignmentId;
        if (!string.IsNullOrWhiteSpace(request.Function)) filter["function"] = request.Function;
        filter["processingState"] = request.Handled switch {
            true => new BsonString("HANDLED"), false => new BsonString("PENDING"),
            null => new BsonDocument("$in", new BsonArray { "PENDING", "HANDLED" }) };
        if (!string.IsNullOrWhiteSpace(request.State)) {
            filter["requiresAction"] = true;
            filter["priority"] = request.State switch { "OVERDUE" => 0, "DUE_SOON" => 1, _ => 2 };
        }
        if (request.DueFromUtc.HasValue || request.DueBeforeUtc.HasValue) {
            var due = new BsonDocument();
            if (request.DueFromUtc.HasValue) due["$gte"] = new BsonDateTime(request.DueFromUtc.Value);
            if (request.DueBeforeUtc.HasValue) due["$lt"] = new BsonDateTime(request.DueBeforeUtc.Value);
            filter["dueAtUtc"] = due;
        }
        if (!string.IsNullOrWhiteSpace(request.SearchText)) {
            var literal = new BsonRegularExpression(Regex.Escape(request.SearchText.Trim()), "i");
            filter["$or"] = new BsonArray { new BsonDocument("assignmentName", literal),
                new BsonDocument("workName", literal), new BsonDocument("title", literal) };
        }
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
                WorkName = work.Name, Title = work.Name, State = "INFORMATION", ProcessingState = "INFORMATION",
                Target = new WorkInboxTarget { Kind = "WORK", WorkId = work.Id } };
        }
        var pipeline = Pipeline(actor, closed: true);
        pipeline.Add(WorkInboxPipeline.Match(new BsonDocument("id", id)));
        pipeline.Add(new BsonDocument("$limit", 1));
        return (await Read(pipeline, ct)).Select(Map).FirstOrDefault();
    }

    private List<string> RecentTypes()
    {
        var types = new List<string> { "WORK_DUE", "ASSIGNMENT_DUE", "REPORT_DUE", "WORK_DUE_SOON", "ASSIGNMENT_DUE_SOON",
            "REPORT_DUE_SOON", "REPORT_APPROVED", "REPORT_RETURNED", "REPORT_REVIEW_REQUIRED", "REPORT_RESUBMITTED", "DYNAMIC_FORM_CLONE_APPROVED", "ASSIGNMENT_HANDOVER_APPROVED", "ASSIGNMENT_ASSIGNED", "ASSIGNMENT_HANDOVER_RECEIVED" };
        return types;
    }

    public async Task<WorkInboxNotificationPage> RecentAsync(string actor, CancellationToken ct)
        => await NotificationPageAsync(await notifications.SearchAsync(new NotificationSearchRequest { PageSize = 20, Types = RecentTypes() }, actor, ct), actor, ct);

    public async Task<WorkInboxNotificationPage> HistoryAsync(NotificationSearchRequest request, string actor, CancellationToken ct)
        => await NotificationPageAsync(await notifications.SearchAsync(request, actor, ct), actor, ct);

    public Task<WorkInboxNotificationGroupPage> RecentNotificationGroupsAsync(string actor, CancellationToken ct)
        => NotificationGroupsAsync(new NotificationSearchRequest { PageSize = 20, Types = RecentTypes() }, actor, ct);

    public Task MarkNotificationGroupsReadAsync(IReadOnlyCollection<WorkInboxNotificationGroupReadDescriptor> groups, string actor, CancellationToken ct)
        => new WorkInboxNotificationGroups(ctx, notifications).MarkReadAsync(groups, actor, ct);

    public async Task<WorkInboxNotificationGroupPage> NotificationGroupsAsync(NotificationSearchRequest request, string actor, CancellationToken ct)
    {
        var page = await new WorkInboxNotificationGroups(ctx, notifications).SearchAsync(request, actor, ct);
        // Reuse subject enrichment and current-source checks only for one representative per page group.
        await NotificationPageAsync(new NotificationSearchResponse { Items = page.Items.Select(x => x.Notification).ToList() }, actor, ct);
        var groupedReports = page.Items.Where(x => WorkInboxNotificationGroups.ReportTypes.Contains(x.Notification.Type)
            && (x.GroupKey.StartsWith("assignment:", StringComparison.Ordinal) || x.GroupKey.StartsWith("work:", StringComparison.Ordinal))).ToList();
        if (groupedReports.Count == 0) return page;
        string Function(WorkInboxNotificationGroup group) => group.Notification.Type is "REPORT_REVIEW_REQUIRED" or "REPORT_RESUBMITTED" ? "REVIEW" : "REPORT";
        BsonDocument Scope(WorkInboxNotificationGroup group) => new() {
            { group.GroupKey.StartsWith("assignment:", StringComparison.Ordinal) ? "assignmentId" : "workId", group.GroupKey.Split(':')[1] },
            { "function", Function(group) } };
        var assignmentIds = groupedReports.All(x => x.GroupKey.StartsWith("assignment:", StringComparison.Ordinal))
            ? groupedReports.Select(x => x.GroupKey.Split(':')[1]).Distinct().ToList() : null;
        var pipeline = Pipeline(actor, closed: true, assignmentIds: assignmentIds);
        pipeline.Add(WorkInboxPipeline.Match(new BsonDocument("$or", new BsonArray(groupedReports.Select(Scope)))));
        pipeline.Add(new BsonDocument("$group", new BsonDocument {
            { "_id", new BsonDocument { { "assignmentId", "$assignmentId" }, { "workId", "$workId" }, { "function", "$function" } } },
            { "total", new BsonDocument("$sum", 1) },
            { "pending", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$processingState", "PENDING" }), 1, 0 })) },
            { "handled", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$processingState", "HANDLED" }), 1, 0 })) },
            { "information", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$processingState", "INFORMATION" }), 1, 0 })) },
            { "priority", new BsonDocument("$min", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$processingState", "PENDING" }), "$priority", 2 })) }
        }));
        var sourceRows = await Read(pipeline, ct);
        foreach (var group in groupedReports) {
            var key = group.GroupKey.Split(':');
            var matching = sourceRows.Where(x => Text(x["_id"].AsBsonDocument, key[0] == "assignment" ? "assignmentId" : "workId") == key[1]
                && Text(x["_id"].AsBsonDocument, "function") == Function(group)).ToList();
            var total = matching.Sum(x => x["total"].ToInt64());
            var pending = matching.Sum(x => x["pending"].ToInt64());
            var handled = matching.Sum(x => x["handled"].ToInt64());
            var information = matching.Sum(x => x["information"].ToInt64());
            group.Notification.ProcessingState = pending > 0 ? "PENDING" : total > 0 && handled == total ? "HANDLED"
                : information > 0 ? "INFORMATION" : "UNAVAILABLE";
            group.Notification.ProcessingPriority = pending > 0 ? matching.Min(x => x["priority"].ToInt32()) : null;
            if (key[0] == "assignment" && matching.Count > 0)
                group.Notification.WorkId = Text(matching[0]["_id"].AsBsonDocument, "workId");
            // Aggregate report events open the assignment/function scope, never an arbitrary latest member.
            group.ItemId = key[0] + ":" + key[1];
        }
        return page;
    }

    // Resolve legacy subjects in two bounded batch reads, after recipient-scoped pagination.
    // No schema, report payload, or form name is used as the work subject.
    private async Task<WorkInboxNotificationPage> NotificationPageAsync(NotificationSearchResponse page, string actor, CancellationToken ct)
    {
        var assignmentIds = page.Items.Select(x => x.WorkAssignmentId).Where(x => ObjectId.TryParse(x, out _)).Distinct().ToList();
        var assignments = assignmentIds.Count == 0 ? [] : await ctx.WorkAssignments
            .Find(x => assignmentIds.Contains(x.Id) && !x.IsDeleted)
            .Project(x => new { x.Id, x.Name, x.WorkId }).ToListAsync(ct);
        var workIds = page.Items.Select(x => x.WorkId).Concat(assignments.Select(x => x.WorkId))
            .Where(x => ObjectId.TryParse(x, out _)).Distinct().ToList();
        var works = workIds.Count == 0 ? [] : await ctx.Works.Find(x => workIds.Contains(x.Id) && !x.IsDeleted)
            .Project(x => new { x.Id, x.Name }).ToListAsync(ct);
        var assignmentMap = assignments.ToDictionary(x => x.Id);
        var workNames = works.ToDictionary(x => x.Id, x => x.Name);
        foreach (var row in page.Items)
        {
            var assignment = row.WorkAssignmentId is not null ? assignmentMap.GetValueOrDefault(row.WorkAssignmentId) : null;
            if (string.IsNullOrWhiteSpace(row.AssignmentName)) row.AssignmentName = assignment?.Name;
            var workId = string.IsNullOrWhiteSpace(row.WorkId) ? assignment?.WorkId : row.WorkId;
            if (string.IsNullOrWhiteSpace(row.WorkId) && assignment is not null) row.WorkId = assignment.WorkId;
            if (string.IsNullOrWhiteSpace(row.WorkName) && workId is not null && workNames.TryGetValue(workId, out var workName)) row.WorkName = workName;
        }
        // One actor-scoped aggregate for at most one page of linked sources. Historical read flags
        // and historical RequiresAction snapshots never decide today's processing badge.
        var itemIds = page.Items.Select(NotificationItemId).Where(x => x is not null && !x.StartsWith("work:", StringComparison.Ordinal))
            .Distinct().Select(x => new BsonString(x)).ToList();
        var processing = new Dictionary<string, (string State, int? Priority)>();
        if (itemIds.Count > 0) {
            var pipeline = Pipeline(actor, closed: true, itemIds: itemIds.Select(x => x.AsString).ToList());
            pipeline.Add(WorkInboxPipeline.Match(new BsonDocument("id", new BsonDocument("$in", new BsonArray(itemIds)))));
            pipeline.Add(new BsonDocument("$project", new BsonDocument { { "_id", 0 }, { "id", 1 }, { "processingState", 1 }, { "priority", 1 } }));
            processing = (await Read(pipeline, ct)).ToDictionary(x => Text(x, "id")!, x => (
                Text(x, "processingState") ?? "INFORMATION",
                Text(x, "processingState") == "PENDING" ? (int?)x.GetValue("priority", 2).ToInt32() : null));
        }
        foreach (var row in page.Items) {
            var id = NotificationItemId(row);
            var current = id is null || id.StartsWith("work:", StringComparison.Ordinal) ? (State: "INFORMATION", Priority: (int?)null)
                : processing.GetValueOrDefault(id, ("UNAVAILABLE", null));
            row.ProcessingState = current.State;
            row.ProcessingPriority = current.Priority;
        }
        return NotificationPage(page);
    }

    public async Task<InboxAssignmentDetail?> AssignmentDetailAsync(string id, string actor, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out _)) return null;
        // Same scope as the historical assignment item; seek one assignment by ID instead of rebuilding the whole queue.
        var filter = Builders<WorkAssignment>.Filter;
        var reviewerMissing = filter.Eq(x => x.CurrentReviewerUserId, null)
            | new BsonDocument("currentReviewerUserId", ""); // Legacy empty string must not serialize as an ObjectId.
        var scope = filter.ElemMatch(x => x.Assignees, u => u.UserId == actor)
            | filter.Eq(x => x.CurrentReviewerUserId, actor)
            | (reviewerMissing & filter.Eq(x => x.CreatedByUserId, actor));
        var a = await ctx.WorkAssignments.Find(filter.Eq(x => x.Id, id) & filter.Eq(x => x.IsActive, true)
            & filter.Eq(x => x.IsDeleted, false) & filter.Eq(x => x.FlowInstanceId, null) & scope).FirstOrDefaultAsync(ct);
        if (a is null) return null;
        var work = await ctx.Works.Find(x => x.Id == a.WorkId && !x.IsDeleted).FirstOrDefaultAsync(ct);
        if (work is null) return null;
        var refs = await UserRefSnapshotHelper.LoadUserRefMapAsync(ctx,
            new[] { a.CreatedByUserId, a.CurrentReviewerUserId }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!), ct);
        var receiver = a.Assignees.FirstOrDefault(x => x.UserId == actor);
        var form = ObjectId.TryParse(a.DynamicFormTemplateId, out _) ? await ctx.DynamicFormTemplates
            .Find(x => x.Id == a.DynamicFormTemplateId && !x.IsDeleted).Project(x => new { x.Id, x.CreatedByUserId }).FirstOrDefaultAsync(ct) : null;
        var approved = form is not null && await ctx.DynamicFormCloneRequests.Find(x => x.DynamicFormTemplateId == form.Id
            && x.RequesterUserId == actor && x.Status == "APPROVED" && !x.IsDeleted).Limit(1).AnyAsync(ct);
        var actorMetadata = await ctx.Users.Find(x => x.Id == actor && !x.IsDeleted)
            .Project(x => new { x.AccountKind, x.Roles }).FirstOrDefaultAsync(ct);
        var admin = string.Equals(actorMetadata?.AccountKind, "SYSTEM_ADMIN", StringComparison.OrdinalIgnoreCase)
            || actorMetadata?.Roles.Any(x => string.Equals(x, "SYSTEM_ADMIN", StringComparison.OrdinalIgnoreCase)) == true;
        var canClone = form is not null && (form.CreatedByUserId == actor || admin || approved);
        var request = await ctx.DynamicFormCloneRequests.Find(x => x.WorkAssignmentId == id
            && x.DynamicFormTemplateId == a.DynamicFormTemplateId && x.RequesterUserId == actor && !x.IsDeleted)
            .SortByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        return new InboxAssignmentDetail {
            AssignmentId = a.Id, AssignmentName = string.IsNullOrWhiteSpace(a.Name) ? work.Name : a.Name,
            WorkName = work.Name, Description = a.Description, Issuer = refs.GetValueOrDefault(a.CreatedByUserId ?? ""),
            Reviewer = refs.GetValueOrDefault(a.CurrentReviewerUserId ?? a.CreatedByUserId ?? ""),
            Recipients = a.Assignees, IsRecipient = receiver is not null, FormName = a.DynamicFormTemplateName ?? a.DynamicExcelName,
            FormVersion = a.DynamicFormVersionNo, StartDate = a.StartDate, DueAtUtc = a.DueAtUtc,
            ProgressStatus = a.ProgressStatus,
            State = tdtd_be.Services.WorkAssignments.Progress.WorkExecutionProgressPolicy.IsCompleted(a) ? "COMPLETED"
                : a.CompletionReviewPeriodId != null && a.CompletionReopenedAtUtc.HasValue ? "REOPENED"
                : work.Status == WorkStatus.S3 || work.CompletedAtUtc.HasValue ? "PARENT_COMPLETED" : "OPEN",
            CanClone = canClone, CloneStatus = form is null ? "UNAVAILABLE" : canClone ? "GRANTED" : request?.Status ?? "NOT_REQUESTED",
            CanRequestClone = form is not null && receiver is not null && !canClone && request?.Status != "PENDING"
                && !string.IsNullOrWhiteSpace(a.CreatedByUserId) && a.CreatedByUserId != actor
        };
    }

    private static WorkInboxNotificationPage NotificationPage(NotificationSearchResponse page) => new() {
        Items = page.Items.Select(row => new WorkInboxNotification { Notification = row, ItemId = NotificationItemId(row) }).ToList(),
        HasMore = page.HasMore, NextCursorOccurredAtUtc = page.NextCursorOccurredAtUtc, NextCursorId = page.NextCursorId,
        RecentUnreadCount = page.Items.Count(x => x.ReadAtUtc is null)
    };

    public static string? NotificationItemId(NotificationRowDto row)
    {
        if (!string.IsNullOrWhiteSpace(row.RequestId) && row.Type.StartsWith("DYNAMIC_FORM_CLONE", StringComparison.Ordinal)) return "clone:" + row.RequestId;
        if (!string.IsNullOrWhiteSpace(row.WorkReportPeriodId)) return (row.Type is "REPORT_REVIEW_REQUIRED" or "REPORT_RESUBMITTED" ? "review:" : "report:") + row.WorkReportPeriodId;
        if (!string.IsNullOrWhiteSpace(row.WorkAssignmentId)) return "assignment:" + row.WorkAssignmentId;
        return string.IsNullOrWhiteSpace(row.WorkId) ? null : "work:" + row.WorkId;
    }

    private static string? Text(BsonDocument row, string field) => row.TryGetValue(field, out var value) && !value.IsBsonNull ? value.AsString : null;
    private static DateTime? Date(BsonDocument row, string field) => row.TryGetValue(field, out var value) && value.IsBsonDateTime ? value.ToUniversalTime() : null;
    private static WorkInboxItem Map(BsonDocument row)
    {
        var actionable = row.GetValue("requiresAction", false).AsBoolean;
        var processingState = Text(row, "processingState") ?? (actionable ? "PENDING" : "INFORMATION");
        var title = Text(row, "title");
        if (string.IsNullOrWhiteSpace(title)) title = Text(row, "assignmentName");
        if (string.IsNullOrWhiteSpace(title)) title = Text(row, "workName");
        if (string.IsNullOrWhiteSpace(title)) title = "Công việc cần thực hiện";
        return new WorkInboxItem {
            Id = Text(row, "id")!, Function = Text(row, "function")!, Title = title,
            Description = Text(row, "description"), CanRequestClone = row.GetValue("canRequestClone", false).AsBoolean,
            WorkId = Text(row, "workId")!, WorkName = Text(row, "workName") ?? "Công việc",
            AssignmentId = Text(row, "assignmentId"), AssignmentCode = Text(row, "assignmentCode"), AssignmentName = string.IsNullOrWhiteSpace(Text(row, "assignmentName")) ? Text(row, "workName") : Text(row, "assignmentName"),
            PeriodKey = Text(row, "periodKey"), AssigneeName = Text(row, "assigneeName"), DueAtUtc = Date(row, "dueAtUtc"),
            State = Text(row, "state") ?? "OPEN", RequiresAction = actionable,
            ProcessingState = processingState,
            ReportStatus = row.TryGetValue("reportStatus", out var reportStatus) && reportStatus.IsNumeric ? reportStatus.ToInt32() : null,
            NeedsResubmission = row.GetValue("needsResubmission", false).AsBoolean,
            IsResubmission = row.GetValue("isResubmission", false).AsBoolean,
            Priority = row.GetValue("priority", 2).ToInt32(), SortDueAtUtc = Date(row, "sortDueAtUtc") ?? DateTime.MaxValue,
            Target = new WorkInboxTarget { Kind = Text(row, "targetKind")!, WorkId = Text(row, "workId")!, AssignmentId = Text(row, "assignmentId"),
                PeriodId = Text(row, "periodId"), ReportId = processingState == "UNAVAILABLE" ? null : Text(row, "reportId"), AssigneeUserId = Text(row, "assigneeUserId"),
                // Processing state is not review authority. Reviewer rows are scoped to
                // the current reviewer by the pipeline; mutations recheck every guard.
                RequestId = Text(row, "requestId"), ReadOnly = processingState == "UNAVAILABLE" ||
                    (!actionable && Text(row, "function") != "REVIEW") }
        };
    }
}
