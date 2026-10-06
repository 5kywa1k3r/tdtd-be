using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using tdtd_be.Common.Time;
using tdtd_be.Models;

internal static class CompletionDateChecks
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var (instant, expected) in new[] {
            (new DateTime(2026,10,5,16,59,59,DateTimeKind.Utc), new DateTime(2026,10,5,0,0,0,DateTimeKind.Utc)),
            (new DateTime(2026,10,5,17,0,0,DateTimeKind.Utc), new DateTime(2026,10,6,0,0,0,DateTimeKind.Utc)),
            (new DateTime(2026,12,31,17,0,0,DateTimeKind.Utc), new DateTime(2027,1,1,0,0,0,DateTimeKind.Utc)),
            (new DateTime(2028,2,28,17,0,0,DateTimeKind.Utc), new DateTime(2028,2,29,0,0,0,DateTimeKind.Utc)) })
        {
            var day=WorkCompletionDate.FromUtc(instant);
            check(day==expected && day.Kind==DateTimeKind.Utc, "Completion civil day at Vietnam boundary "+instant.ToString("O"));
            var assignment=new WorkAssignment{CompletedDate=day,CompletedAtUtc=instant,CompletionMode="APPROVED_REQUEST"};
            var bson=assignment.ToBsonDocument();
            var read=BsonSerializer.Deserialize<WorkAssignment>(bson);
            check(bson["completedDate"].ToUniversalTime()==expected && read.CompletedDate==expected,
                "Assignment BSON preserves civil completion day "+expected.ToString("yyyy-MM-dd"));
            var work=BsonSerializer.Deserialize<Work>(new Work{CompletedDate=day,CompletedAtUtc=instant}.ToBson());
            check(work.CompletedDate==expected,"Work BSON preserves automatic completion day "+expected.ToString("yyyy-MM-dd"));
        }
        var at=new DateTime(2026,10,6,9,0,0,DateTimeKind.Utc);
        var old=new DateTime(2026,10,5,17,0,0,DateTimeKind.Utc);
        var expectedDay=new DateTime(2026,10,6,0,0,0,DateTimeKind.Utc);
        foreach(var mode in new[]{"APPROVED_REQUEST","AUTO_REPORTS_AND_DEADLINE"}) {
            check(WorkCompletionDate.Read(old,at,mode)==expectedDay,"Legacy workflow BSON read uses committed instant: "+mode);
            check(WorkCompletionDate.Read(expectedDay,at,mode)==expectedDay,"Canonical workflow day is not shifted twice: "+mode);
        }
        check(WorkCompletionDate.Read(old,at,null)==old,"Legacy untagged date is preserved");
        check(WorkCompletionDate.Read(old,at,"WORK_OWNER")==old,"User chosen Work date is not replaced by decision time");
        check(WorkCompletionDate.Read(old,null,"APPROVED_REQUEST")==old,"Missing decision instant never fabricates a date");
        check(WorkCompletionDate.Read(null,at,"APPROVED_REQUEST")==null,"Cleared completion date remains absent");
    }
}
