using MongoDB.Bson;

namespace tdtd_be.Services.WorkInbox;

// Source rows establish current responsibility, including the current reviewer after handover.
// Neither notification read state nor a bounded notification lookback defines this queue.
public static class WorkInboxPipeline
{
    public static List<BsonDocument> Build(ObjectId actor, string assignments, string works,
        string periods, string clones, string reports, string logs, DateTime now,
        double? dueSoonHours, bool includeClosed = false)
    {
        var ownPeriods = new List<BsonDocument>
        {
            Match(new BsonDocument { { "isDeleted", false }, { "isActive", true }, { "assigneeUserId", actor } }),
        };
        if (!includeClosed) ownPeriods.Add(Match(new BsonDocument("status", new BsonDocument("$in", new BsonArray { 0, 1, 4, 5 }))));
        ownPeriods.AddRange(AssignmentAndWork(assignments, works, "workAssignmentId", includeClosed));
        ownPeriods.Add(Match(new BsonDocument("$expr", new BsonDocument("$in", new BsonArray { actor,
            IfNull("$a.assignees.userId", new BsonArray()) }))));
        ownPeriods.Add(Lookup(reports, "currentReportId", "currentReport"));
        ownPeriods.Add(new BsonDocument("$unwind", new BsonDocument { { "path", "$currentReport" }, { "preserveNullAndEmptyArrays", true } }));
        if (!includeClosed) ownPeriods.Add(Match(new BsonDocument("$or", new BsonArray {
            new BsonDocument("currentReportId", BsonNull.Value),
            new BsonDocument { { "currentReport.isDeleted", false }, { "currentReport.isActive", true }, { "currentReport.isCurrent", true }, { "currentReport.status", 0 } }
        })));
        ownPeriods.Add(Project("report", "REPORT", "$reportTitle", "$dueAtUtc", "REPORT_PERIOD", includeClosed,
            new BsonDocument { { "periodId", Str("$_id") }, { "reportId", Str("$currentReportId") },
                { "periodKey", "$periodKey" }, { "assigneeUserId", Str("$assigneeUserId") },
                { "sourceStatus", new BsonDocument("$cond", new BsonArray {
                    new BsonDocument("$or", new BsonArray { Eq(IfNull("$currentReportId", BsonNull.Value), BsonNull.Value), Eq("$currentReport.status", 0) }), "$status", 999 }) },
                { "description", "$returnReason" }, { "canRequestClone", CloneEligibility(actor) } }));

        var reviewPeriods = new List<BsonDocument>
        {
            Match(new BsonDocument { { "isDeleted", false }, { "isActive", true }, { "flowInstanceId", BsonNull.Value },
                { "$or", new BsonArray { new BsonDocument("currentReviewerUserId", actor),
                    new BsonDocument { { "currentReviewerUserId", new BsonDocument("$in", new BsonArray { BsonNull.Value, "" }) }, { "createdByUserId", actor } } } } }),
            new BsonDocument("$set", new BsonDocument("a", "$$ROOT")),
            new BsonDocument("$lookup", new BsonDocument { { "from", periods }, { "localField", "_id" }, { "foreignField", "workAssignmentId" }, { "as", "p" } }), Unwind("p"),
            Match(new BsonDocument { { "p.isDeleted", false }, { "p.isActive", true } })
        };
        if (!includeClosed) reviewPeriods.Add(Match(new BsonDocument("p.status", new BsonDocument("$in", new BsonArray { 2, 6 }))));
        reviewPeriods.Add(Lookup(works, "a.workId", "w")); reviewPeriods.Add(Unwind("w"));
        reviewPeriods.Add(Match(new BsonDocument("w.isDeleted", false)));
        if (!includeClosed) reviewPeriods.Add(Match(new BsonDocument("w.status", new BsonDocument("$ne", 3))));
        reviewPeriods.Add(Lookup(reports, "p.currentReportId", "r"));
        reviewPeriods.Add(Unwind("r"));
        reviewPeriods.Add(Match(new BsonDocument { { "r.isDeleted", false }, { "r.isActive", true }, { "r.isCurrent", true } }));
        reviewPeriods.Add(Match(new BsonDocument("$expr", new BsonDocument("$and", new BsonArray {
            Eq("$r.workAssignmentId", "$a._id"), Eq("$r.workReportPeriodId", "$p._id"), Eq("$r.assigneeUserId", "$p.assigneeUserId"),
            new BsonDocument("$in", new BsonArray { "$p.assigneeUserId", IfNull("$a.assignees.userId", new BsonArray()) }) }))));
        if (!includeClosed) reviewPeriods.Add(Match(new BsonDocument("r.status", 1)));
        reviewPeriods.Add(new BsonDocument("$lookup", new BsonDocument {
            { "from", logs }, { "let", new BsonDocument { { "period", "$p._id" }, { "submitted", "$p.lastSubmittedAtUtc" } } },
            { "pipeline", new BsonArray { Match(new BsonDocument { { "isDeleted", false }, { "action", new BsonDocument("$in", new BsonArray { "RETURN", "REVIEW_RETURN" }) },
                { "$expr", new BsonDocument("$and", new BsonArray {
                    new BsonDocument("$eq", new BsonArray { "$workReportPeriodId", "$$period" }),
                    new BsonDocument("$lt", new BsonArray { "$actionAtUtc", "$$submitted" }) }) } }),
                new BsonDocument("$limit", 1) } }, { "as", "returns" } }));
        reviewPeriods.Add(new BsonDocument("$set", new BsonDocument("u", new BsonDocument("$arrayElemAt", new BsonArray {
            new BsonDocument("$filter", new BsonDocument { { "input", "$a.assignees" }, { "as", "user" }, { "cond", Eq("$$user.userId", "$p.assigneeUserId") } }), 0 }))));
        reviewPeriods.Add(Project("review", "REVIEW", "$p.reportTitle", "$p.dueAtUtc", "REVIEW", includeClosed,
            new BsonDocument { { "periodId", Str("$p._id") }, { "reportId", Str("$r._id") },
                { "periodKey", "$p.periodKey" }, { "assigneeUserId", Str("$p.assigneeUserId") },
                { "assigneeName", IfNull("$u.fullName", "$u.username") },
                { "sourceStatus", new BsonDocument("$cond", new BsonArray { Eq("$r.status", 1), "$p.status", 999 }) },
                { "isResubmission", new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$returns"), 0 }) } }, "$p._id"));

        // Historical assignment notifications also belong to the current reviewer.
        // This scope is for context links only; it does not add a reviewer writing obligation.
        var assignmentContextScope = includeClosed
            ? new BsonDocument("$or", new BsonArray {
                new BsonDocument("assignees.userId", actor),
                new BsonDocument("currentReviewerUserId", actor),
                new BsonDocument { { "currentReviewerUserId", new BsonDocument("$in", new BsonArray { BsonNull.Value, "" }) }, { "createdByUserId", actor } } })
            : new BsonDocument("assignees.userId", actor);
        assignmentContextScope["isDeleted"] = false;
        assignmentContextScope["isActive"] = true;
        assignmentContextScope["flowInstanceId"] = BsonNull.Value;
        var ownAssignments = new List<BsonDocument> {
            Match(assignmentContextScope),
            new BsonDocument("$set", new BsonDocument("a", "$$ROOT")),
            Lookup(works, "workId", "w"), Unwind("w"), Match(new BsonDocument("w.isDeleted", false))
        };
        if (!includeClosed) ownAssignments.Add(Match(new BsonDocument {
            { "progressStatus", new BsonDocument("$ne", 2) }, { "completedAtUtc", BsonNull.Value }, { "completedDate", BsonNull.Value },
            { "dynamicFormTemplateId", BsonNull.Value }, { "dynamicExcelId", BsonNull.Value }, { "w.status", new BsonDocument("$ne", 3) } }));
        ownAssignments.Add(Project("assignment", "ASSIGNMENT", "$name", "$dueAtUtc", "ASSIGNMENT", includeClosed,
            new BsonDocument { { "canRequestClone", CloneEligibility(actor) }, { "sourceStatus", new BsonDocument("$cond", new BsonArray {
                new BsonDocument("$or", new BsonArray {
                    new BsonDocument("$not", new BsonArray { new BsonDocument("$in", new BsonArray { actor, IfNull("$assignees.userId", new BsonArray()) }) }),
                    new BsonDocument("$ne", new BsonArray { IfNull("$completedAtUtc", BsonNull.Value), BsonNull.Value }),
                    new BsonDocument("$ne", new BsonArray { IfNull("$completedDate", BsonNull.Value), BsonNull.Value }), Eq("$w.status", 3) }), 2, "$progressStatus" }) } }));

        var cloneScope = includeClosed
            ? new BsonDocument("$or", new BsonArray { new BsonDocument("assignmentOwnerUserId", actor), new BsonDocument("requesterUserId", actor) })
            : new BsonDocument("assignmentOwnerUserId", actor);
        cloneScope["isDeleted"] = false;
        var pendingClones = new List<BsonDocument> { Match(cloneScope) };
        if (!includeClosed) pendingClones.Add(Match(new BsonDocument("status", "PENDING")));
        pendingClones.AddRange(AssignmentAndWork(assignments, works, "workAssignmentId", includeClosed));
        pendingClones.Add(Project("clone", "REQUEST", "$dynamicFormTemplateName", BsonNull.Value, "CLONE_REQUEST", includeClosed,
            new BsonDocument { { "requestId", Str("$_id") }, { "assigneeName", IfNull("$requester.fullName", "$requester.username") },
                { "description", "$requestReason" }, { "sourceStatus", "$status" }, { "isRequestOwner", Eq("$assignmentOwnerUserId", actor) } }));

        ownPeriods.Add(Union(assignments, reviewPeriods));
        ownPeriods.Add(Union(assignments, ownAssignments));
        ownPeriods.Add(Union(clones, pendingClones));
        // Projection retries can leave duplicate reviewer candidates. Count one source obligation.
        ownPeriods.Add(new BsonDocument("$group", new BsonDocument { { "_id", "$id" }, { "row", new BsonDocument("$first", "$$ROOT") } }));
        ownPeriods.Add(new BsonDocument("$replaceRoot", new BsonDocument("newRoot", "$row")));
        var open = new BsonDocument("$switch", new BsonDocument {
            { "branches", new BsonArray {
                Branch(Eq("$function", "REPORT"), In("$sourceStatus", new BsonArray { 0, 1, 4, 5 })),
                Branch(Eq("$function", "REVIEW"), In("$sourceStatus", new BsonArray { 2, 6 })),
                Branch(Eq("$function", "REQUEST"), new BsonDocument("$and", new BsonArray { Eq("$sourceStatus", "PENDING"), "$isRequestOwner" })) } },
            { "default", new BsonDocument("$ne", new BsonArray { "$sourceStatus", 2 }) } });
        var sourceOpen = new BsonDocument("$and", new BsonArray { open,
            new BsonDocument("$ne", new BsonArray { "$workClosed", true }),
            new BsonDocument("$or", new BsonArray { Eq("$function", "REQUEST"), new BsonDocument("$ne", new BsonArray { "$assignmentClosed", true }) }) });
        ownPeriods.Add(new BsonDocument("$set", new BsonDocument { { "requiresAction", sourceOpen },
            { "sortDueAtUtc", IfNull("$dueAtUtc", new BsonDateTime(DateTime.MaxValue)) } }));
        if (!includeClosed) ownPeriods.Add(Match(new BsonDocument("requiresAction", true)));
        var deadline = new BsonDocument("$and", new BsonArray { "$requiresAction", new BsonDocument("$ne", new BsonArray { "$dueAtUtc", BsonNull.Value }) });
        var late = new BsonDocument("$and", new BsonArray { deadline, new BsonDocument("$lt", new BsonArray { "$dueAtUtc", new BsonDateTime(now) }) });
        BsonValue soon = dueSoonHours.HasValue ? new BsonDocument("$and", new BsonArray { deadline,
            new BsonDocument("$lte", new BsonArray { "$dueAtUtc", new BsonDateTime(now.AddHours(dueSoonHours.Value)) }) }) : BsonBoolean.False;
        ownPeriods.Add(new BsonDocument("$set", new BsonDocument {
            { "priority", new BsonDocument("$switch", new BsonDocument { { "branches", new BsonArray { Branch(late, 0), Branch(soon, 1) } }, { "default", 2 } }) },
            { "state", new BsonDocument("$switch", new BsonDocument { { "branches", new BsonArray {
                Branch(new BsonDocument("$not", new BsonArray { "$requiresAction" }), "RESOLVED"),
                Branch(Eq("$function", "REVIEW"), "WAITING_REVIEW"), Branch(late, "OVERDUE"), Branch(soon, "DUE_SOON") } }, { "default", "OPEN" } }) } }));
        return ownPeriods;
    }

    private static IEnumerable<BsonDocument> AssignmentAndWork(string assignments, string works, string field, bool closed)
    {
        yield return Lookup(assignments, field, "a"); yield return Unwind("a");
        yield return Match(new BsonDocument { { "a.isDeleted", false }, { "a.isActive", true }, { "a.flowInstanceId", BsonNull.Value } });
        yield return Lookup(works, "a.workId", "w"); yield return Unwind("w");
        yield return Match(new BsonDocument("w.isDeleted", false));
        if (!closed) yield return Match(new BsonDocument("w.status", new BsonDocument("$ne", 3)));
    }

    private static BsonDocument Project(string prefix, string function, BsonValue title, BsonValue due, string target,
        bool closed, BsonDocument extra, string idField = "$_id")
    {
        var row = new BsonDocument { { "_id", 0 }, { "id", new BsonDocument("$concat", new BsonArray { prefix + ":", Str(idField) }) },
            { "function", new BsonDocument("$literal", function) }, { "title", IfNull(title, IfNull("$a.name", "$a.dynamicFormTemplateName")) },
            { "workId", Str("$w._id") }, { "workName", "$w.name" }, { "assignmentId", Str("$a._id") },
            { "assignmentCode", "$a.code" }, { "assignmentName", IfNull("$a.name", "$a.dynamicFormTemplateName") },
            { "workClosed", Eq("$w.status", 3) },
            { "assignmentClosed", new BsonDocument("$or", new BsonArray { Eq("$a.progressStatus", 2),
                new BsonDocument("$ne", new BsonArray { IfNull("$a.completedAtUtc", BsonNull.Value), BsonNull.Value }),
                new BsonDocument("$ne", new BsonArray { IfNull("$a.completedDate", BsonNull.Value), BsonNull.Value }) }) },
            { "dueAtUtc", due is BsonNull ? new BsonDocument("$literal", BsonNull.Value) : IfNull(due, BsonNull.Value) },
            { "targetKind", new BsonDocument("$literal", target) } };
        foreach (var element in extra) row[element.Name] = element.Value;
        return new BsonDocument("$project", row);
    }

    public static BsonDocument Match(BsonDocument filter) => new("$match", filter);
    private static BsonDocument CloneEligibility(ObjectId actor) => new("$and", new BsonArray {
        new BsonDocument("$ne", new BsonArray { IfNull("$a.dynamicFormTemplateId", BsonNull.Value), BsonNull.Value }),
        new BsonDocument("$ne", new BsonArray { IfNull("$a.createdByUserId", BsonNull.Value), BsonNull.Value }),
        new BsonDocument("$ne", new BsonArray { "$a.createdByUserId", actor }),
        new BsonDocument("$in", new BsonArray { actor, IfNull("$a.assignees.userId", new BsonArray()) }) });
    public static BsonDocument Sort() => new("$sort", new BsonDocument { { "priority", 1 }, { "sortDueAtUtc", 1 }, { "id", 1 } });
    public static BsonDocument Str(BsonValue value) => new("$convert", new BsonDocument { { "input", value }, { "to", "string" }, { "onNull", BsonNull.Value } });
    private static BsonDocument IfNull(BsonValue value, BsonValue fallback) => new("$ifNull", new BsonArray { value, fallback });
    private static BsonDocument Eq(BsonValue a, BsonValue b) => new("$eq", new BsonArray { a, b });
    private static BsonDocument In(BsonValue a, BsonArray b) => new("$in", new BsonArray { a, b });
    private static BsonDocument Branch(BsonValue condition, BsonValue value) => new() { { "case", condition }, { "then", value } };
    private static BsonDocument Lookup(string collection, string local, string alias) => new("$lookup", new BsonDocument {
        { "from", collection }, { "localField", local }, { "foreignField", "_id" }, { "as", alias } });
    private static BsonDocument Unwind(string alias) => new("$unwind", "$" + alias);
    private static BsonDocument Union(string collection, List<BsonDocument> pipeline) => new("$unionWith", new BsonDocument {
        { "coll", collection }, { "pipeline", new BsonArray(pipeline) } });
}
