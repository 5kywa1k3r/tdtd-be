using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

// Test-only, temporary revocation for a browser already holding a preview.
// No new actor is granted access. Restore uses a single-field compare-and-set.
internal static class ListL4AclProbe
{
    internal static async Task Run(string reportId,string evidenceDirectory)
    {
        var db=new MongoDbContext(Options.Create(new MongoOptions{ConnectionString="mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000",Database="tdtd"}));
        var report=await db.WorkAssignmentReports.Find(r=>r.Id==reportId).SingleAsync();
        var work=await db.Works.Find(w=>w.Id==report.WorkId).SingleAsync();
        var actor=await db.Users.Find(u=>u.Id==report.AssigneeUserId).SingleAsync();
        if(!work.AutoCode.StartsWith("list-l4-",StringComparison.Ordinal)||!actor.Username.StartsWith("list-l4-",StringComparison.Ordinal)||report.Status!=WorkAssignmentReportStatus.Draft)
            throw new InvalidOperationException("ACL probe requires an isolated L4 draft fixture.");
        var binding=await db.WorkTemplateAssignees.Find(b=>b.WorkId==work.Id&&b.WorkAssignmentId==report.WorkAssignmentId&&b.AssigneeUserId==actor.Id&&!b.IsDeleted).SingleAsync();
        Directory.CreateDirectory(evidenceDirectory);
        var prefix=Path.Combine(evidenceDirectory,"acl-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmss"));
        var release=prefix+".release";
        await File.WriteAllTextAsync(prefix+"-backup.json",JsonSerializer.Serialize(new{reportId,workId=work.Id,bindingId=binding.Id,assigneeUserId=binding.AssigneeUserId,report.PayloadRevision,report.PayloadHash,release},new JsonSerializerOptions{WriteIndented=true}));
        var changed=false;
        try
        {
            var update=await db.WorkTemplateAssignees.UpdateOneAsync(b=>b.Id==binding.Id&&b.WorkId==work.Id&&b.AssigneeUserId==actor.Id&&!b.IsDeleted,
                Builders<WorkTemplateAssignee>.Update.Set(b=>b.AssigneeUserId,string.Empty));
            changed=update.ModifiedCount==1;
            if(!changed)throw new InvalidOperationException("Fixture ACL compare-and-set failed.");
            Console.WriteLine($"REVOKED fixture binding={binding.Id}; releaseFile={release}; automatic restore in 180 seconds.");
            var until=DateTime.UtcNow.AddSeconds(180);
            while(!File.Exists(release)&&DateTime.UtcNow<until)await Task.Delay(250);
        }
        finally
        {
            if(changed)
            {
                var restored=await db.WorkTemplateAssignees.UpdateOneAsync(b=>b.Id==binding.Id&&b.WorkId==work.Id&&b.AssigneeUserId==string.Empty,
                    Builders<WorkTemplateAssignee>.Update.Set(b=>b.AssigneeUserId,actor.Id));
                if(restored.ModifiedCount!=1)throw new InvalidOperationException("Fixture ACL restore compare-and-set failed; inspect the backup, never overwrite a concurrent owner.");
                var after=await db.WorkAssignmentReports.Find(r=>r.Id==reportId).SingleAsync();
                var unchanged=after.PayloadRevision==report.PayloadRevision&&after.PayloadHash==report.PayloadHash&&after.LifecycleRevision==report.LifecycleRevision;
                await File.WriteAllTextAsync(prefix+"-restored.json",JsonSerializer.Serialize(new{reportId,bindingId=binding.Id,restored=true,payloadAndLifecycleUnchanged=unchanged},new JsonSerializerOptions{WriteIndented=true}));
                if(!unchanged)throw new InvalidOperationException("Report changed during ACL rejection probe.");
                Console.WriteLine("PASS ACL restored by compare-and-set; report payload and lifecycle unchanged.");
            }
        }
    }
}
