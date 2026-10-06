using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.Notifications;
using tdtd_be.DTOs.WorkInbox;
using tdtd_be.Models;
using tdtd_be.Services.Notifications;

namespace tdtd_be.Services.WorkInbox;

// Group notifications before paging, while retaining each independent non-report action.
public sealed class WorkInboxNotificationGroups(MongoDbContext ctx, INotificationService notifications)
{
    public static readonly HashSet<string> ReportTypes = new(StringComparer.Ordinal) {
        "REPORT_REVIEW_REQUIRED", "REPORT_RESUBMITTED", "REPORT_RETURNED", "REPORT_DUE", "REPORT_DUE_SOON", "REPORT_PERIOD_AT_RISK", "REPORT_PERIOD_OVERDUE", "REPORT_APPROVED" };

    private static bool TryKey(string? key, out string kind, out string id, out string? type)
    {
        var parts = (key ?? "").Split(':'); kind = parts.ElementAtOrDefault(0) ?? "";
        id = parts.ElementAtOrDefault(1) ?? ""; type = parts.ElementAtOrDefault(2);
        return ObjectId.TryParse(id, out var parsed) && parsed.ToString() == id &&
            (kind == "notification" && parts.Length == 2 || kind is "assignment" or "work" && parts.Length == 3 && type is not null && ReportTypes.Contains(type));
    }
    public static bool ValidGroupKey(string? key) => TryKey(key, out _, out _, out _);
    public static DateTime CompletedCreationWatermark(DateTime utcNow) => new(
        utcNow.ToUniversalTime().Ticks - utcNow.ToUniversalTime().Ticks % TimeSpan.TicksPerMillisecond - TimeSpan.TicksPerMillisecond,
        DateTimeKind.Utc);

    private static FilterDefinition<UserNotification> Scope(NotificationSearchRequest request, string actor)
    {
        var f = Builders<UserNotification>.Filter;
        var filter = f.Eq(x => x.RecipientUserId, actor) & f.Eq(x => x.IsDeleted, false);
        if (!string.IsNullOrWhiteSpace(request.WorkId)) filter &= f.Eq(x => x.WorkId, request.WorkId.Trim());
        if (!string.IsNullOrWhiteSpace(request.WorkAssignmentId)) filter &= f.Eq(x => x.WorkAssignmentId, request.WorkAssignmentId.Trim());
        if (request.UnreadOnly == true) filter &= f.Eq(x => x.ReadAtUtc, null);
        else if (request.UnreadOnly == false) filter &= f.Ne(x => x.ReadAtUtc, null);
        if (request.OccurredFromUtc.HasValue) filter &= f.Gte(x => x.OccurredAtUtc, request.OccurredFromUtc.Value);
        if (request.OccurredBeforeUtc.HasValue) filter &= f.Lt(x => x.OccurredAtUtc, request.OccurredBeforeUtc.Value);
        var types = (request.Types ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToUpperInvariant()).Distinct().ToList();
        if (types.Count > 0) filter &= f.In(x => x.Type, types);
        if (!string.IsNullOrWhiteSpace(request.Category)) filter &= f.Eq(x => x.Category, request.Category.Trim().ToUpperInvariant());
        if (request.RequiresAction.HasValue) filter &= f.Eq(x => x.RequiresAction, request.RequiresAction.Value);
        if (!string.IsNullOrWhiteSpace(request.ActionState)) filter &= f.Eq(x => x.ActionState, request.ActionState.Trim().ToUpperInvariant());
        return filter;
    }

    public async Task<WorkInboxNotificationGroupPage> SearchAsync(NotificationSearchRequest request, string actor, CancellationToken ct)
    {
        // Mongo dates have millisecond precision. Exclude the current, incomplete millisecond
        // so a fresh arrival created while this snapshot is read cannot share its watermark.
        var asOfUtc = CompletedCreationWatermark(DateTime.UtcNow);
        var filter = (Scope(request, actor) & Builders<UserNotification>.Filter.Lte(x => x.CreatedAtUtc, asOfUtc))
            .Render(new RenderArgs<UserNotification>(ctx.Notifications.DocumentSerializer, ctx.Notifications.Settings.SerializerRegistry));
        BsonDocument Present(string field) => new("$regexMatch", new BsonDocument {
            { "input", new BsonDocument("$convert", new BsonDocument { { "input", field }, { "to", "string" }, { "onError", "" }, { "onNull", "" } }) },
            { "regex", "^[0-9a-fA-F]{24}$" } });
        BsonDocument Key(string prefix, string field, bool eventKey) => new("$concat", eventKey
            ? new BsonArray { prefix + ":", new BsonDocument("$toLower", WorkInboxPipeline.Str(field)), ":", "$type" }
            : new BsonArray { prefix + ":", WorkInboxPipeline.Str(field) });
        var reportKey = new BsonDocument("$cond", new BsonArray { Present("$workAssignmentId"), Key("assignment", "$workAssignmentId", true),
            new BsonDocument("$cond", new BsonArray { Present("$workId"), Key("work", "$workId", true), Key("notification", "$_id", false) }) });
        var pipeline = new List<BsonDocument> {
            WorkInboxPipeline.Match(filter),
            new("$sort", new BsonDocument { { "occurredAtUtc", -1 }, { "_id", -1 } }),
            new("$set", new BsonDocument("groupKey", new BsonDocument("$cond", new BsonArray {
                new BsonDocument("$in", new BsonArray { "$type", new BsonArray(ReportTypes) }), reportKey, Key("notification", "$_id", false) }))),
            new("$group", new BsonDocument { { "_id", "$groupKey" }, { "latest", new BsonDocument("$first", "$$ROOT") },
                { "total", new BsonDocument("$sum", 1) }, { "unreadCount", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray {
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$readAtUtc", BsonNull.Value }), BsonNull.Value }), 1, 0 })) } }),
            new("$set", new BsonDocument("occurredAtUtc", "$latest.occurredAtUtc"))
        };
        if (request.CursorId is not null) pipeline.Add(WorkInboxPipeline.Match(new BsonDocument("$or", new BsonArray {
            new BsonDocument("occurredAtUtc", new BsonDocument("$lt", new BsonDateTime(request.CursorOccurredAtUtc!.Value))),
            new BsonDocument { { "occurredAtUtc", new BsonDateTime(request.CursorOccurredAtUtc!.Value) }, { "_id", new BsonDocument("$lt", request.CursorId) } } })));
        pipeline.Add(new("$sort", new BsonDocument { { "occurredAtUtc", -1 }, { "_id", -1 } }));
        var size = Math.Clamp(request.PageSize, 1, 50);
        if (request.PageNumber.HasValue) pipeline.Add(new("$skip", checked((request.PageNumber.Value - 1) * size)));
        pipeline.Add(new("$limit", size + 1));
        var rows = await ctx.Notifications.Aggregate<BsonDocument>(pipeline,
            new AggregateOptions { AllowDiskUse = true, MaxTime = TimeSpan.FromSeconds(20) }).ToListAsync(ct);
        var items = rows.Take(size).Select(row => {
            var latest = BsonSerializer.Deserialize<UserNotification>(row["latest"].AsBsonDocument);
            return new WorkInboxNotificationGroup { Notification = NotificationService.ToDto(latest),
                ItemId = WorkInboxService.NotificationItemId(NotificationService.ToDto(latest)), GroupKey = row["_id"].AsString,
                Total = row["total"].ToInt64(), UnreadCount = row["unreadCount"].ToInt64(), AsOfUtc = asOfUtc };
        }).ToList();
        var more = rows.Count > size; var last = items.LastOrDefault();
        return new() { Items = items, HasMore = more, NextCursorOccurredAtUtc = more ? last?.Notification.OccurredAtUtc : null,
            NextCursorId = more ? last?.GroupKey : null, RecentUnreadCount = checked((int)items.Sum(x => x.UnreadCount)) };
    }

    public async Task MarkReadAsync(IReadOnlyCollection<WorkInboxNotificationGroupReadDescriptor> groups, string actor, CancellationToken ct)
    {
        if (groups.Count == 0) return;
        var f = Builders<UserNotification>.Filter;
        FilterDefinition<UserNotification> EntityId(string field, string id) => f.Or(new BsonDocument(field, ObjectId.Parse(id)),
            new BsonDocument(field, new BsonRegularExpression("^" + id + "$", "i")));
        var scopes = groups.Select(group => {
            TryKey(group.GroupKey, out var kind, out var id, out var type);
            var scope = kind switch {
                "assignment" => EntityId("workAssignmentId", id) & f.Eq(x => x.Type, type),
                "work" => EntityId("workId", id) & (f.Eq(x => x.WorkAssignmentId, null) | new BsonDocument("workAssignmentId", "")
                    | f.Regex("workAssignmentId", new BsonRegularExpression("^(?![0-9a-fA-F]{24}$).*$"))) & f.Eq(x => x.Type, type),
                _ => f.Eq(x => x.Id, id) };
            return scope & f.Lte(x => x.CreatedAtUtc, group.AsOfUtc);
        });
        var filter = f.Eq(x => x.RecipientUserId, actor) & f.Eq(x => x.IsDeleted, false) & f.Eq(x => x.ReadAtUtc, null) & f.Or(scopes);
        using var cursor = await ctx.Notifications.Find(filter).SortBy(x => x.Id).Project(x => x.Id).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var ids in cursor.Current.Chunk(50))
            await notifications.MarkManyReadAsync(ids, actor, ct);
    }
}
