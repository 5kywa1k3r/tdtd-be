using MongoDB.Bson;

namespace tdtd_be.Services.WorkInbox;

// Source rows establish current responsibility, including the current reviewer after handover.
// Neither notification read state nor a bounded notification lookback defines this queue.
public static class WorkInboxPipeline
{
    public static List<BsonDocument> Build(ObjectId actor, string assignments, string works,
        string periods, string clones, string reports, string logs, string templates, DateTime now,
        double? dueSoonHours, bool includeClosed = false, IReadOnlyCollection<string>? itemIds = null,
        IReadOnlyCollection<string>? assignmentIds = null)
    {
        BsonDocument IdScope(string prefix, string field = "_id") => new(field, new BsonDocument("$in", new BsonArray(
            itemIds!.Where(x => x.StartsWith(prefix + ":", StringComparison.Ordinal))
                .Select(x => x[(prefix.Length + 1)..]).Where(x => ObjectId.TryParse(x, out _)).Select(x => ObjectId.Parse(x)))));
        var ownPeriods = new List<BsonDocument>
        {
            Match(new BsonDocument { { "isDeleted", false }, { "isActive", true }, { "assigneeUserId", actor } }),
        };
        if (itemIds is not null) ownPeriods.Add(Match(IdScope("report")));
        if (assignmentIds is not null) ownPeriods.Add(Match(new BsonDocument("workAssignmentId", new BsonDocument("$in", new BsonArray(assignmentIds.Select(ObjectId.Parse))))));
        if (!includeClosed) ownPeriods.Add(Match(new BsonDocument("status", new BsonDocument("$in", new BsonArray { 0, 1, 4, 5 }))));
        // Resolve the branch once per assignment, not once for every daily period.
        ownPeriods.Add(new BsonDocument("$group", new BsonDocument { { "_id", "$workAssignmentId" },
            { "periodRows", new BsonDocument("$push", "$$ROOT") } }));
        ownPeriods.AddRange(AssignmentAndWork(assignments, works, "_id", includeClosed));
        ownPeriods.Add(Unwind("periodRows"));
        ownPeriods.Add(new BsonDocument("$replaceRoot", new BsonDocument("newRoot", new BsonDocument("$mergeObjects", new BsonArray {
            "$periodRows", new BsonDocument { { "a", "$a" }, { "w", "$w" },
                { "scopeAncestors", "$scopeAncestors" }, { "scopeChainValid", "$scopeChainValid" } } }))));
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
                { "sourceHandled", In("$currentReport.status", new BsonArray { 1, 2 }) },
                { "sourceValid", new BsonDocument("$or", new BsonArray {
                    Eq(IfNull("$currentReportId", BsonNull.Value), BsonNull.Value),
                    new BsonDocument("$and", new BsonArray { Eq("$currentReport.isDeleted", false),
                        Eq("$currentReport.isActive", true), Eq("$currentReport.isCurrent", true),
                        Eq("$currentReport.workAssignmentId", "$a._id"), Eq("$currentReport.workReportPeriodId", "$_id"),
                        Eq("$currentReport.assigneeUserId", "$assigneeUserId") }) }) },
                { "sourceStatus", new BsonDocument("$cond", new BsonArray {
                    new BsonDocument("$or", new BsonArray { Eq(IfNull("$currentReportId", BsonNull.Value), BsonNull.Value), Eq("$currentReport.status", 0) }), "$status", 999 }) },
                { "reportStatus", "$currentReport.status" },
                { "needsResubmission", new BsonDocument("$and", new BsonArray { Eq("$currentReport.status", 0),
                    new BsonDocument("$ne", new BsonArray { IfNull("$currentReport.returnReason", ""), "" }) }) },
                { "description", "$currentReport.returnReason" }, { "canRequestClone", CloneEligibility(actor) } }));

        var reviewPeriods = new List<BsonDocument>
        {
            Match(new BsonDocument { { "isDeleted", false }, { "isActive", true }, { "flowInstanceId", BsonNull.Value },
                { "$or", new BsonArray { new BsonDocument("currentReviewerUserId", actor),
                    new BsonDocument { { "currentReviewerUserId", new BsonDocument("$in", new BsonArray { BsonNull.Value, "" }) }, { "createdByUserId", actor } } } } }),
            new BsonDocument("$set", new BsonDocument("a", "$$ROOT"))
        };
        if (assignmentIds is not null) reviewPeriods.Insert(1, Match(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(assignmentIds.Select(ObjectId.Parse))))));
        reviewPeriods.Add(Lookup(works, "a.workId", "w")); reviewPeriods.Add(Unwind("w"));
        reviewPeriods.Add(Match(new BsonDocument("w.isDeleted", false)));
        reviewPeriods.AddRange(Ancestors(assignments));
        reviewPeriods.Add(new BsonDocument("$lookup", new BsonDocument { { "from", periods }, { "localField", "_id" }, { "foreignField", "workAssignmentId" }, { "as", "p" } }));
        reviewPeriods.Add(Unwind("p"));
        reviewPeriods.Add(Match(new BsonDocument { { "p.isDeleted", false }, { "p.isActive", true } }));
        if (itemIds is not null) reviewPeriods.Add(Match(IdScope("review", "p._id")));
        if (!includeClosed) reviewPeriods.Add(Match(new BsonDocument("p.status", new BsonDocument("$in", new BsonArray { 2, 6 }))));
        reviewPeriods.Add(Lookup(reports, "p.currentReportId", "r"));
        reviewPeriods.Add(Unwind("r"));
        reviewPeriods.Add(Match(new BsonDocument { { "r.isDeleted", false }, { "r.isActive", true }, { "r.isCurrent", true } }));
        reviewPeriods.Add(Match(new BsonDocument("$expr", new BsonDocument("$and", new BsonArray {
            Eq("$r.workAssignmentId", "$a._id"), Eq("$r.workReportPeriodId", "$p._id"), Eq("$r.assigneeUserId", "$p.assigneeUserId"),
            new BsonDocument("$in", new BsonArray { "$p.assigneeUserId", IfNull("$a.assignees.userId", new BsonArray()) }) }))));
        if (!includeClosed) reviewPeriods.Add(Match(new BsonDocument("r.status", 1)));
        reviewPeriods.Add(new BsonDocument("$lookup", new BsonDocument {
            { "from", logs }, { "let", new BsonDocument { { "period", "$p._id" }, { "submitted", "$p.lastSubmittedAtUtc" } } },
            { "pipeline", new BsonArray { Match(new BsonDocument { { "isDeleted", false }, { "action", new BsonDocument("$in", new BsonArray { "RETURN", "REVIEW_RETURN", "Trả lại" }) },
                { "$expr", new BsonDocument("$and", new BsonArray {
                    new BsonDocument("$eq", new BsonArray { "$workReportPeriodId", "$$period" }),
                    new BsonDocument("$lt", new BsonArray { "$actionAtUtc", "$$submitted" }) }) } }),
                new BsonDocument("$limit", 1) } }, { "as", "returns" } }));
        reviewPeriods.Add(new BsonDocument("$set", new BsonDocument("u", new BsonDocument("$arrayElemAt", new BsonArray {
            new BsonDocument("$filter", new BsonDocument { { "input", "$a.assignees" }, { "as", "user" }, { "cond", Eq("$$user.userId", "$p.assigneeUserId") } }), 0 }))));
        reviewPeriods.Add(Project("review", "REVIEW", "$p.reportTitle", "$p.dueAtUtc", "REVIEW", includeClosed,
            new BsonDocument { { "periodId", Str("$p._id") }, { "reportId", Str("$r._id") },
                { "periodKey", "$p.periodKey" }, { "assigneeUserId", Str("$p.assigneeUserId") },
                { "queueEligible", In("$r.status", new BsonArray { 1, 2 }) },
                { "sourceHandled", Eq("$r.status", 2) }, { "reportStatus", "$r.status" },
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
        if (itemIds is not null) ownAssignments.Insert(1, Match(IdScope("assignment")));
        if (assignmentIds is not null) ownAssignments.Insert(1, Match(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(assignmentIds.Select(ObjectId.Parse))))));
        ownAssignments.AddRange(Ancestors(assignments));
        if (!includeClosed) ownAssignments.Add(Match(new BsonDocument {
            { "dynamicFormTemplateId", BsonNull.Value }, { "dynamicExcelId", BsonNull.Value } }));
        ownAssignments.Add(Project("assignment", "ASSIGNMENT", "$name", "$dueAtUtc", "ASSIGNMENT", includeClosed,
            new BsonDocument { { "canRequestClone", CloneEligibility(actor) },
                { "queueEligible", new BsonDocument("$and", new BsonArray {
                    new BsonDocument("$in", new BsonArray { actor, IfNull("$assignees.userId", new BsonArray()) }),
                    Eq(IfNull("$dynamicFormTemplateId", BsonNull.Value), BsonNull.Value),
                    Eq(IfNull("$dynamicExcelId", BsonNull.Value), BsonNull.Value) }) },
                { "sourceStatus", new BsonDocument("$cond", new BsonArray {
                new BsonDocument("$or", new BsonArray {
                    new BsonDocument("$not", new BsonArray { new BsonDocument("$in", new BsonArray { actor, IfNull("$assignees.userId", new BsonArray()) }) }),
                    Completed("$a"), WorkClosed() }), 2, "$progressStatus" }) } }));

        var cloneScope = includeClosed
            ? new BsonDocument("$or", new BsonArray { new BsonDocument("assignmentOwnerUserId", actor), new BsonDocument("requesterUserId", actor) })
            : new BsonDocument("assignmentOwnerUserId", actor);
        cloneScope["isDeleted"] = false;
        var pendingClones = new List<BsonDocument> { Match(cloneScope) };
        if (itemIds is not null) pendingClones.Add(Match(IdScope("clone")));
        if (assignmentIds is not null) pendingClones.Add(Match(new BsonDocument("workAssignmentId", new BsonDocument("$in", new BsonArray(assignmentIds.Select(ObjectId.Parse))))));
        if (!includeClosed) pendingClones.Add(Match(new BsonDocument("status", "PENDING")));
        pendingClones.AddRange(AssignmentAndWork(assignments, works, "workAssignmentId", includeClosed));
        pendingClones.Add(new BsonDocument("$lookup", new BsonDocument {
            { "from", templates }, { "let", new BsonDocument("source", "$dynamicFormTemplateId") },
            { "pipeline", new BsonArray { Match(new BsonDocument { { "isDeleted", false },
                { "$expr", Eq("$_id", "$$source") } }), new BsonDocument("$project", new BsonDocument("_id", 1)) } },
            { "as", "cloneSource" } }));
        var validCloneSource = new BsonDocument("$and", new BsonArray {
            Eq("$dynamicFormTemplateId", "$a.dynamicFormTemplateId"),
            new BsonDocument("$in", new BsonArray { "$requesterUserId", IfNull("$a.assignees.userId", new BsonArray()) }),
            new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$cloneSource"), 0 }) });
        if (!includeClosed) pendingClones.Add(Match(new BsonDocument("$expr", validCloneSource)));
        pendingClones.Add(Project("clone", "REQUEST", "$dynamicFormTemplateName", BsonNull.Value, "CLONE_REQUEST", includeClosed,
            new BsonDocument { { "requestId", Str("$_id") }, { "assigneeName", IfNull("$requester.fullName", "$requester.username") },
                { "description", new BsonDocument("$cond", new BsonArray { validCloneSource, "$requestReason", "Yêu cầu không còn hiệu lực với nguồn hoặc người nhận hiện tại." }) },
                { "sourceStatus", new BsonDocument("$cond", new BsonArray { validCloneSource, "$status", "UNAVAILABLE" }) },
                { "sourceValid", validCloneSource },
                { "sourceHandled", In("$status", new BsonArray { "APPROVED", "REJECTED" }) },
                { "isRequestOwner", Eq("$assignmentOwnerUserId", actor) } }));

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
        var sourceOpen = new BsonDocument("$and", new BsonArray { open, IfNull("$sourceValid", true),
            "$mutationScopeOpen" });
        ownPeriods.Add(new BsonDocument("$set", new BsonDocument { { "requiresAction", sourceOpen },
            { "sortDueAtUtc", IfNull("$dueAtUtc", new BsonDateTime(DateTime.MaxValue)) } }));
        // Non-actionable context (e.g. a requester awaiting approval or a reviewer's assignment link)
        // is informational. Only an actual completed source obligation belongs in handled results.
        ownPeriods.Add(new BsonDocument("$set", new BsonDocument("processingState", new BsonDocument("$switch", new BsonDocument {
            { "branches", new BsonArray {
                Branch(Eq(IfNull("$sourceValid", true), false), "UNAVAILABLE"),
                Branch(new BsonDocument("$and", new BsonArray { "$requiresAction", IfNull("$queueEligible", true) }), "PENDING"),
                Branch(new BsonDocument("$and", new BsonArray { IfNull("$queueEligible", true),
                    new BsonDocument("$or", new BsonArray { IfNull("$sourceHandled", false),
                        new BsonDocument("$and", new BsonArray { new BsonDocument("$ne", new BsonArray { "$function", "REQUEST" }),
                            new BsonDocument("$or", new BsonArray { "$workClosed", "$assignmentClosed" }) }) }) }), "HANDLED") } },
            { "default", "INFORMATION" } }))));
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
        foreach (var stage in Ancestors(assignments)) yield return stage;
    }

    private static BsonDocument Project(string prefix, string function, BsonValue title, BsonValue due, string target,
        bool closed, BsonDocument extra, string idField = "$_id")
    {
        var row = new BsonDocument { { "_id", 0 }, { "id", new BsonDocument("$concat", new BsonArray { prefix + ":", Str(idField) }) },
            { "function", new BsonDocument("$literal", function) }, { "title", IfNull(title, IfNull("$a.name", "$w.name")) },
            { "workId", Str("$w._id") }, { "workName", "$w.name" }, { "assignmentId", Str("$a._id") },
            { "assignmentCode", "$a.code" }, { "assignmentName", IfNull("$a.name", "$w.name") },
            { "workClosed", WorkClosed() },
            { "assignmentClosed", Completed("$a") },
            { "mutationScopeOpen", ScopeOpen(function is "REPORT" or "REVIEW" ? idField : null, function == "REQUEST") },
            { "dueAtUtc", due is BsonNull ? new BsonDocument("$literal", BsonNull.Value) : IfNull(due, BsonNull.Value) },
            { "targetKind", new BsonDocument("$literal", target) } };
        foreach (var element in extra) row[element.Name] = element.Value;
        return new BsonDocument("$project", row);
    }

    public static BsonDocument Match(BsonDocument filter) => new("$match", filter);
    // Same predicate as WorkExecutionScopeGuard: effective ancestor chain first, then
    // completion, with an exception for the exact reopened report period only.
    private static IEnumerable<BsonDocument> Ancestors(string assignments)
    {
        // ParentAssignmentId is stored as a string, unlike _id. $graphLookup cannot
        // convert its recursive connect fields. Read only this Work's small scope headers,
        // then follow canonical parents; no payloads, Path trust or silent depth cutoff.
        yield return new("$lookup", new BsonDocument { { "from", assignments },
            { "let", new BsonDocument("work", "$a.workId") }, { "pipeline", new BsonArray {
                Match(new BsonDocument("$expr", Eq("$workId", "$$work"))),
                new BsonDocument("$project", new BsonDocument { { "_id", Str("$_id") }, { "workId", 1 }, { "parentAssignmentId", 1 },
                    { "isActive", 1 }, { "isDeleted", 1 }, { "completedAtUtc", 1 }, { "completedDate", 1 }, { "progressStatus", 1 } }) } },
            { "as", "scopeNodes" } });
        var next = new BsonDocument("$arrayElemAt", new BsonArray { new BsonDocument("$filter", new BsonDocument {
            { "input", "$scopeNodes" }, { "as", "node" }, { "cond", Eq("$$node._id", "$$value.id") } }), 0 });
        var step = new BsonDocument("$let", new BsonDocument { { "vars", new BsonDocument("next", next) },
            { "in", new BsonDocument { { "id", IfNull("$$next.parentAssignmentId", "") },
                { "valid", new BsonDocument("$and", new BsonArray { "$$value.valid", Present("$$next._id") }) },
                { "rows", new BsonDocument("$concatArrays", new BsonArray { "$$value.rows", new BsonDocument("$cond", new BsonArray {
                    Present("$$next._id"), new BsonArray { "$$next" }, new BsonArray() }) }) } } } });
        yield return new("$set", new BsonDocument("scopeWalk", new BsonDocument("$reduce", new BsonDocument {
            { "input", "$scopeNodes" }, { "initialValue", new BsonDocument { { "id", IfNull("$a.parentAssignmentId", "") },
                { "valid", true }, { "rows", new BsonArray() } } },
            { "in", new BsonDocument("$cond", new BsonArray { Eq("$$value.id", ""), "$$value", step }) } })));
        yield return new("$set", new BsonDocument { { "scopeAncestors", "$scopeWalk.rows" },
            { "scopeChainValid", new BsonDocument("$and", new BsonArray { "$scopeWalk.valid", Eq("$scopeWalk.id", "") }) } });
        yield return new("$unset", new BsonArray { "scopeWalk", "scopeNodes" });
    }
    private static BsonDocument Not(BsonValue value) => new("$not", new BsonArray { value });
    private static BsonDocument Present(string field) => Not(Eq(IfNull(field, BsonNull.Value), BsonNull.Value));
    private static BsonDocument Completed(string prefix) => new("$or", new BsonArray {
        Present(prefix + ".completedAtUtc"), new BsonDocument("$and", new BsonArray {
            Eq(prefix + ".progressStatus", 2), Present(prefix + ".completedDate") }) });
    private static BsonDocument WorkClosed() => new("$or", new BsonArray { Eq("$w.status", 3), Present("$w.completedAtUtc") });
    private static BsonDocument ScopeOpen(string? period, bool clone)
    {
        var ids = IfNull("$scopeAncestors._id", new BsonArray());
        BsonDocument ParentPresent(string parent) => new("$or", new BsonArray {
            Eq(IfNull(parent, BsonNull.Value), BsonNull.Value), Eq(parent, ""), new BsonDocument("$in", new BsonArray { parent, ids }) });
        var effective = new BsonDocument("$and", new BsonArray {
            Eq("$a.isActive", true), Eq("$a.isDeleted", false), Eq("$w.isDeleted", false),
            "$scopeChainValid", Not(new BsonDocument("$in", new BsonArray { Str("$a._id"), ids })), ParentPresent("$a.parentAssignmentId"),
            new BsonDocument("$or", new BsonArray {
                Eq(IfNull("$a.parentAssignmentId", ""), ""),
                new BsonDocument("$anyElementTrue", new BsonArray { new BsonDocument("$map", new BsonDocument {
                    { "input", "$scopeAncestors" }, { "as", "ancestor" },
                    { "in", Eq(IfNull("$$ancestor.parentAssignmentId", ""), "") } }) }) }),
            new BsonDocument("$allElementsTrue", new BsonArray { new BsonDocument("$map", new BsonDocument {
                { "input", "$scopeAncestors" }, { "as", "ancestor" }, { "in", new BsonDocument("$and", new BsonArray {
                    Eq("$$ancestor.workId", "$a.workId"), Eq("$$ancestor.isActive", true), Eq("$$ancestor.isDeleted", false),
                    ParentPresent("$$ancestor.parentAssignmentId") }) } }) }) });
        var ancestorsCompleted = new BsonDocument("$anyElementTrue", new BsonArray { new BsonDocument("$map", new BsonDocument {
            { "input", "$scopeAncestors" }, { "as", "ancestor" }, { "in", Completed("$$ancestor") } }) });
        BsonValue reopened = period == null ? BsonBoolean.False : new BsonDocument("$and", new BsonArray {
            Present("$a.completionReopenedAtUtc"), Eq("$a.completionReviewPeriodId", Str(period)),
            Not(new BsonDocument("$gt", new BsonArray { IfNull("$w.completedAtUtc", BsonNull.Value), "$a.completionReopenedAtUtc" })),
            new BsonDocument("$allElementsTrue", new BsonArray { new BsonDocument("$map", new BsonDocument {
                { "input", "$scopeAncestors" }, { "as", "ancestor" },
                { "in", Not(new BsonDocument("$gt", new BsonArray {
                    IfNull("$$ancestor.completedAtUtc", BsonNull.Value), "$a.completionReopenedAtUtc" })) } }) }) });
        return new("$and", new BsonArray { effective, clone ? BsonBoolean.True : Not(Completed("$a")),
            new BsonDocument("$or", new BsonArray { reopened, new BsonDocument("$and", new BsonArray { Not(WorkClosed()), Not(ancestorsCompleted) }) }) });
    }
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
