using MongoDB.Bson;
using tdtd_be.DashboardModel.DTOs;

namespace tdtd_be.DashboardModel.Services;

// Database equivalent of DashboardLeadershipProjection. These expressions are also
// exercised against literal documents in read-only Mongo aggregation checks.
internal static class DashboardLeadershipDbProjection
{
    internal static BsonDocument E(string op, params BsonValue[] values) => new(op, new BsonArray(values));
    internal static BsonDocument N(BsonValue value, BsonValue fallback) => E("$ifNull", value, fallback);
    internal static BsonDocument If(BsonValue condition, BsonValue yes, BsonValue no) => E("$cond", condition, yes, no);
    internal static BsonDocument Day(BsonValue value, bool utc = false) => new("$dateToString", new BsonDocument
        { { "date", value }, { "format", "%Y-%m-%d" }, { "timezone", utc ? "+07:00" : "UTC" }, { "onNull", BsonNull.Value } });
    internal static BsonDocument Id(BsonValue value) => new("$toString", value);
    internal static BsonDocument Present(BsonValue value) => E("$ne", N(value, BsonNull.Value), BsonNull.Value);
    internal static BsonDocument Cases(BsonValue fallback, params (BsonValue When, BsonValue Then)[] branches)
        => new("$switch", new BsonDocument { { "branches", new BsonArray(branches.Select(x => new BsonDocument { { "case", x.When }, { "then", x.Then } })) }, { "default", fallback } });
    internal static BsonDocument Timing(BsonValue done, BsonValue completed, BsonValue due, BsonValue risk, BsonValue late, string today)
        => Cases(If(Present(due), "ON_TIME", "UNASSESSED"),
            (done, If(E("$and", Present(completed), Present(due)), If(E("$gt", completed, due), "LATE", "ON_TIME"), "UNASSESSED")),
            (E("$or", late, E("$and", Present(due), E("$lt", due, today))), "LATE"), (risk, "AT_RISK"));

    internal static BsonDocument Item(string kind, DateTime now)
    {
        var work = kind == "WORK" ? "$" : "$__work.";
        var assignment = kind == "REPORT" ? "$__assignment." : "$";
        BsonValue W(string field) => work + field;
        BsonValue A(string field) => assignment + field;
        var workDone = E("$eq", W("status"), 3);
        var workDue = Day(N(W("dueDate"), W("endDate")));
        var assignmentDone = E("$or", E("$eq", A("progressStatus"), 2), Present(A("completedAtUtc")));
        var assignmentDue = If(Present(A("dueAtUtc")), Day(A("dueAtUtc"), true), Day(A("dueDate")));
        var projection = new BsonDocument
        {
            { "_id", 0 }, { "Id", Id("$_id") }, { "ObjectKind", kind }, { "WorkId", Id(W("_id")) }, { "WorkType", W("type") },
            { "WorkCode", If(E("$eq", new BsonDocument("$trim", new BsonDocument("input", N(W("code"), ""))), ""), W("autoCode"), W("code")) },
            { "WorkName", W("name") }, { "WorkStatus", W("status") }, { "Priority", W("priority") },
            { "Status", W("status") }, { "StartDate", Day(W("startDate")) }, { "DueDate", workDue },
            { "CompletedDate", If(workDone, Day(W("completedDate")), BsonNull.Value) },
            { "Timeliness", Timing(workDone, Day(W("completedDate")), workDue, E("$eq", W("status"), 4), E("$eq", W("status"), 5), DashboardLeadershipProjection.Day(DashboardLeadershipProjection.BusinessToday(now))) },
            { "__CreatedDay", Day(W("createdAtUtc"), true) },
        };
        if (kind != "WORK")
        {
            projection["AssignmentId"] = Id(A("_id"));
            projection["AssignmentName"] = If(E("$ne", new BsonDocument("$trim", new BsonDocument("input", N(A("name"), ""))), ""), A("name"), N(A("dynamicFormTemplateName"), N(A("dynamicExcelName"), A("code"))));
            projection["Status"] = A("progressStatus");
            projection["StartDate"] = Day(A("startDate")); projection["DueDate"] = assignmentDue;
            projection["CompletedDate"] = If(assignmentDone, Day(A("completedDate")), BsonNull.Value);
            projection["Timeliness"] = Timing(assignmentDone, Day(A("completedDate")), assignmentDue, E("$eq", A("progressStatus"), 3), E("$eq", A("progressStatus"), 4), DashboardLeadershipProjection.Day(DashboardLeadershipProjection.BusinessToday(now)));
            projection["__CreatedDay"] = Day(A("createdAtUtc"), true);
        }
        if (kind == "REPORT")
        {
            var valid = Present("$__report._id");
            var reportStatus = Cases("DRAFT", (E("$eq", "$__report.status", 2), "APPROVED"),
                (E("$and", Present("$__report.returnedAtUtc"), E("$eq", "$__report.status", 0)), "RETURNED"), (E("$eq", "$__report.status", 1), "SUBMITTED"));
            var due = N("$__report.dueAtUtc", "$dueAtUtc");
            projection["Id"] = Id(If(valid, "$__report._id", "$_id"));
            projection["ObjectKind"] = If(valid, "REPORT", "REPORT_PERIOD");
            projection["ReportId"] = N(Id("$__report._id"), BsonNull.Value);
            projection["ReportPeriodId"] = Id("$_id");
            projection["ReadState"] = If(valid, "READY", If(E("$or", E("$gt", new BsonDocument("$size", "$__candidates"), 0), Present("$currentReportId")), "UNAVAILABLE_CURRENT_REPORT", "MISSING_REPORT"));
            projection["SourceReportStatus"] = N("$__report.status", BsonNull.Value); projection["Status"] = N("$__report.status", BsonNull.Value);
            projection["ReportStatus"] = If(valid, reportStatus, "PENDING");
            projection["Timeliness"] = Cases("UNASSESSED", (E("$eq", "$__report.isLateSubmission", true), "LATE"),
                (E("$not", Present(due)), "UNASSESSED"),
                (Present("$__report.submittedAtUtc"), If(E("$gt", "$__report.submittedAtUtc", due), "LATE", "ON_TIME")),
                (E("$and", valid, E("$in", reportStatus, new BsonArray { "APPROVED", "SUBMITTED" })), "UNASSESSED"),
                (E("$lt", due, new BsonDateTime(now)), "LATE"));
            projection["StartDate"] = BsonNull.Value; projection["CompletedDate"] = BsonNull.Value;
            projection["DueDate"] = Day("$dueAtUtc", true); projection["PeriodKey"] = N("$periodKey", BsonNull.Value);
            projection["AssigneeName"] = new BsonDocument("$let", new BsonDocument {
                { "vars", new BsonDocument("user", new BsonDocument("$arrayElemAt", new BsonArray { new BsonDocument("$filter", new BsonDocument {
                    { "input", N("$__assignment.assignees", new BsonArray()) }, { "as", "u" }, { "cond", E("$eq", "$$u.userId", "$assigneeUserId") } }), 0 })) },
                { "in", N("$$user.fullName", BsonNull.Value) } });
        }
        return projection;
    }

    internal static BsonDocument Selection(DashboardLeadershipItemsRequest req, string selection, DateTime from, DateTime to, DateTime today)
    {
        var filters = new BsonArray();
        void Match(string field, BsonValue value) => filters.Add(new BsonDocument(field, value));
        var month = new DateTime(today.Year, today.Month, 1);
        if (selection.StartsWith("WORK_DEADLINE_"))
        {
            Match("Status", new BsonDocument("$ne", 3));
            var key = selection[14..];
            if (key == "NO_DEADLINE") Match("DueDate", BsonNull.Value);
            else
            {
                var bounds = new BsonDocument("$ne", BsonNull.Value);
                if (key == "OVERDUE") bounds["$lt"] = DashboardLeadershipProjection.Day(today);
                else if (key == "TODAY") bounds["$eq"] = DashboardLeadershipProjection.Day(today);
                else if (key == "NEXT_7_DAYS") { bounds["$gt"] = DashboardLeadershipProjection.Day(today); bounds["$lte"] = DashboardLeadershipProjection.Day(today.AddDays(7)); }
                else if (key == "NEXT_8_30_DAYS") { bounds["$gt"] = DashboardLeadershipProjection.Day(today.AddDays(7)); bounds["$lte"] = DashboardLeadershipProjection.Day(today.AddDays(30)); }
                else bounds["$gt"] = DashboardLeadershipProjection.Day(today.AddDays(30));
                Match("DueDate", bounds);
            }
        }
        else if (selection.StartsWith("REPORT_"))
        {
            var state = selection[7..];
            if (state is "PENDING" or "UNAVAILABLE") Match("ReadState", state == "PENDING" ? "MISSING_REPORT" : "UNAVAILABLE_CURRENT_REPORT");
            else { Match("ReportId", new BsonDocument("$ne", BsonNull.Value)); if (state != "ALL") filters.Add(new BsonDocument("$or", new BsonArray { new BsonDocument("ReportStatus", state), new BsonDocument("Timeliness", state) })); }
        }
        else if (selection.EndsWith("_CREATED") || selection.EndsWith("_COMPLETED"))
            Match(selection.EndsWith("_CREATED") ? "__CreatedDay" : "CompletedDate", new BsonDocument { { "$gte", DashboardLeadershipProjection.Day(from) }, { "$lte", DashboardLeadershipProjection.Day(to) } });
        else if (selection.StartsWith("WORK_") || selection.StartsWith("ASSIGNMENT_"))
        {
            var state = selection[(selection.StartsWith("WORK_") ? 5 : 11)..];
            if (state is not ("ALL" or "STATUS")) Match("Timeliness", state);
        }
        else
        {
            Match(selection == "DIRECT_ASSIGNMENTS" ? "WorkStatus" : "Status", new BsonDocument("$ne", 3));
            if (selection is "OVERDUE" or "OVERDUE_BEFORE_WINDOW") Match("Timeliness", "LATE");
            if (selection == "IN_PROGRESS") Match("Status", 2);
            if (selection == "NO_DEADLINE") Match("DueDate", BsonNull.Value);
            if (selection == "OVERDUE_BEFORE_WINDOW") Match("DueDate", new BsonDocument { { "$ne", BsonNull.Value }, { "$lt", DashboardLeadershipProjection.Day(month) } });
            if (selection == "TIMELINE")
            {
                Match("DueDate", new BsonDocument("$gte", DashboardLeadershipProjection.Day(month)));
                filters.Add(new BsonDocument("$or", new BsonArray { new BsonDocument("StartDate", BsonNull.Value), new BsonDocument("StartDate", new BsonDocument("$lte", DashboardLeadershipProjection.Day(month.AddMonths(1).AddDays(-1)))) }));
            }
        }
        if (req.WorkId is not null) Match("WorkId", req.WorkId);
        if (req.Status.HasValue) Match("Status", req.Status.Value);
        if (req.Priority.HasValue) Match("Priority", req.Priority.Value);
        return filters.Count == 0 ? new() : new("$and", filters);
    }

    internal static BsonDocument Page(int page, int size) => new("$facet", new BsonDocument {
        { "Rows", new BsonArray { new BsonDocument("$set", new BsonDocument {
            { "__overdue", If(E("$and", E("$eq", "$ObjectKind", "WORK"), E("$eq", "$Status", 5)), 1, 0) }, { "__dueSort", N("$DueDate", "9999") } }),
            new BsonDocument("$sort", new BsonDocument { { "__overdue", -1 }, { "Priority", -1 }, { "__dueSort", 1 }, { "Id", 1 } }),
            new BsonDocument("$skip", page * size), new BsonDocument("$limit", size), new BsonDocument("$unset", new BsonArray { "__overdue", "__dueSort", "__CreatedDay" }) } },
        { "Total", new BsonArray { new BsonDocument("$count", "count") } } });

    internal static BsonDocument WorkPrefilter(string selection, DateTime today)
    {
        if (!selection.StartsWith("WORK_DEADLINE_")) return selection is "OPEN" or "OVERDUE" or "IN_PROGRESS" or "NO_DEADLINE" or "OVERDUE_BEFORE_WINDOW" or "TIMELINE"
            ? new("status", new BsonDocument("$ne", 3)) : new();
        var key = selection[14..];
        var result = new BsonDocument("status", new BsonDocument("$ne", 3));
        if (key == "NO_DEADLINE") { result["dueDate"] = BsonNull.Value; result["endDate"] = BsonNull.Value; return result; }
        BsonDateTime Date(int offset) => new(DateTime.SpecifyKind(today.AddDays(offset), DateTimeKind.Utc));
        var range = key switch {
            "OVERDUE" => new BsonDocument { { "$ne", BsonNull.Value }, { "$lt", Date(0) } },
            "TODAY" => new BsonDocument { { "$gte", Date(0) }, { "$lt", Date(1) } },
            "NEXT_7_DAYS" => new BsonDocument { { "$gte", Date(1) }, { "$lt", Date(8) } },
            "NEXT_8_30_DAYS" => new BsonDocument { { "$gte", Date(8) }, { "$lt", Date(31) } },
            _ => new BsonDocument("$gte", Date(31)),
        };
        result["$or"] = new BsonArray { new BsonDocument("dueDate", range), new BsonDocument { { "dueDate", BsonNull.Value }, { "endDate", range } } };
        return result;
    }
}
