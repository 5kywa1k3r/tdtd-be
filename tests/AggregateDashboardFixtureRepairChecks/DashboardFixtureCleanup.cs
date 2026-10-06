using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Services.AggregateMapping.Persistence;

// Yud explicitly authorized deleting these abandoned Dashboard test runs on 06/10.
// Exact IDs and Notes, durable backup, full-document CAS, one transaction; never a DB reset.
internal static class DashboardFixtureCleanup
{
    private static readonly (string Work,string Form,string Note)[] Allowlist=[
        ("6ac3ccd23602cd7682579701","6ac3ccd23602cd7682579707","uat-dashboard-20261005-0100dbbb"),
        ("6ac3ce9f616a5f3ba64aac87","6ac3ce9f616a5f3ba64aac8d","uat-dashboard-20261005-b9052fbb"),
        ("6ac3cfb76c75d8f2ffff107b","6ac3cfb76c75d8f2ffff1081","uat-dashboard-20261005-492396b3")];
    private sealed record Row(string Collection,BsonDocument Document,string Hash);
    private static string Hash(BsonDocument doc)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(doc.ToJson())));
    internal static async Task Run(bool apply)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(4));var ct=timeout.Token;
        var db=new MongoDbContext(Options.Create(new MongoOptions{ConnectionString="mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000",Database="tdtd"}));
        string Name<T>(IMongoCollection<T> c)=>c.CollectionNamespace.CollectionName;
        var baseNames=new[]{Name(db.Works),Name(db.WorkAssignments),Name(db.WorkTemplateAssignees),Name(db.WorkReportPeriods),Name(db.Users),Name(db.Units),Name(db.DynamicFormTemplates),Name(db.DynamicFormSections)};
        var notes=Allowlist.Select(a=>a.Note).ToArray();var noteFilter=new BsonDocument("note",new BsonDocument("$in",new BsonArray(notes)));
        var captured=new List<Row>();
        foreach(var name in baseNames){var docs=await db.Db.GetCollection<BsonDocument>(name).Find(noteFilter).ToListAsync(ct);captured.AddRange(docs.Select(d=>new Row(name,d,Hash(d))));}
        var workName=Name(db.Works);var formName=Name(db.DynamicFormTemplates);
        foreach(var allowed in Allowlist){
            var work=await db.Db.GetCollection<BsonDocument>(workName).Find(new BsonDocument("_id",ObjectId.Parse(allowed.Work))).FirstOrDefaultAsync(ct);
            if(work!=null&&(work.GetValue("note",BsonNull.Value)!=allowed.Note||work.GetValue("autoCode",BsonNull.Value)!=allowed.Note+"-giao-xuong"))throw new Exception("Work identity differs; no delete.");
            var form=await db.Db.GetCollection<BsonDocument>(formName).Find(new BsonDocument("_id",ObjectId.Parse(allowed.Form))).FirstOrDefaultAsync(ct);
            if(form!=null&&form.GetValue("note",BsonNull.Value)!=allowed.Note)throw new Exception("Form is no longer exclusively this fixture; no delete.");
            var runRows=captured.Where(c=>c.Document.GetValue("note",BsonNull.Value)==allowed.Note).ToArray();
            if(runRows.Length==0){if(work!=null||form!=null)throw new Exception("Incomplete fixture identity; no delete.");continue;}
            // The harness has exactly six Works, four assignments, eight users, five units, two slots/periods.
            foreach(var expected in new[]{(Name(db.Works),6),(Name(db.WorkAssignments),4),(Name(db.Users),8),(Name(db.Units),5),(Name(db.WorkTemplateAssignees),2),(Name(db.WorkReportPeriods),2)})
                if(runRows.Count(r=>r.Collection==expected.Item1)!=expected.Item2)throw new Exception("Fixture cardinality changed for "+allowed.Note+" "+expected.Item1+"; no delete.");
            foreach(var row in runRows.Where(r=>r.Collection==Name(db.Works)))
                if(!row.Document.GetValue("autoCode","").AsString.StartsWith(allowed.Note+"-",StringComparison.Ordinal))throw new Exception("Not a generated Work; no delete.");
            foreach(var row in runRows.Where(r=>r.Collection==Name(db.Users)))
                if(row.Document.GetValue("passwordHash","")!="disabled-fixture-login"||!row.Document.GetValue("username","").AsString.StartsWith(allowed.Note+"-",StringComparison.Ordinal))throw new Exception("Not a disabled generated user; no delete.");
        }
        var ids=captured.Select(r=>r.Document["_id"].ToString()!).Concat(Allowlist.Select(a=>a.Form)).Distinct().ToArray();
        var values=new BsonArray(ids.SelectMany(id=>new BsonValue[]{new BsonString(id),new BsonObjectId(ObjectId.Parse(id))}));
        var paths=new[]{"workId","workAssignmentId","assignmentId","rootAssignmentId","parentAssignmentId","dynamicFormTemplateId","formId","templateId","workTemplateAssigneeId","workReportPeriodId","periodId","reportId","targetId","targetReportId","entityId","documentId","docId","scopeId","userId","actorUserId","assigneeUserId","ownerUserId","createdByUserId","updatedByUserId","unitId","parentUnitId","assigneeUnitId","issuedByUnitId","leaderDirectiveUserId","owner.userId","owner.unitId","assignees.userId","assignees.unitId","targetUnitIds","managers.userId","scope.workId","scope.assignmentId","source.workId","target.workId"};
        var refFilter=new BsonDocument("$or",new BsonArray(paths.Select(p=>new BsonDocument(p,new BsonDocument("$in",values)))));
        // Scan the application's known collections plus aggregate state, not arbitrary jobs/system databases.
        var scanNames=new HashSet<string>(baseNames,StringComparer.Ordinal);
        foreach(var property in typeof(MongoDbContext).GetProperties())if(property.PropertyType.IsGenericType&&property.PropertyType.GetGenericTypeDefinition()==typeof(IMongoCollection<>)){
            var value=property.GetValue(db)!;var ns=(CollectionNamespace)value.GetType().GetProperty("CollectionNamespace")!.GetValue(value)!;scanNames.Add(ns.CollectionName);
        }
        foreach(var field in typeof(AggregateCollections).GetFields(System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic))if(field.FieldType==typeof(string))scanNames.Add((string)field.GetValue(null)!);
        var keys=captured.Select(r=>r.Collection+":"+r.Document["_id"]).ToHashSet(StringComparer.Ordinal);
        async Task CheckDependencies(IClientSessionHandle? session){
            foreach(var name in scanNames){
                var collection=db.Db.GetCollection<BsonDocument>(name);
                var rows=session==null?await collection.Find(refFilter).ToListAsync(ct):await collection.Find(session,refFilter).ToListAsync(ct);
                var outside=rows.Where(r=>!keys.Contains(name+":"+r["_id"])).Select(r=>r["_id"].ToString()).ToArray();
                if(outside.Length>0)throw new Exception("Outside reference in "+name+": "+string.Join(",",outside)+"; no delete.");
            }
        }
        await CheckDependencies(null);
        var directory="../outputs/aggregate-operators-20261005";Directory.CreateDirectory(directory);
        var stamp=DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff");
        var counts=new BsonDocument(captured.GroupBy(r=>r.Collection).Select(g=>new BsonElement(g.Key,g.Count())));
        var plan=new BsonDocument{["mode"]=apply?"AUTHORIZED_SCOPED_DELETE":"READ_ONLY_CLEANUP_PLAN",["createdAtUtc"]=DateTime.UtcNow,["database"]="tdtd",["allowlist"]=new BsonArray(Allowlist.Select(a=>new BsonDocument{["workId"]=a.Work,["formId"]=a.Form,["note"]=a.Note})),["counts"]=counts,["outsideReferences"]=0,["rows"]=new BsonArray(captured.Select(r=>new BsonDocument{["collection"]=r.Collection,["sha256"]=r.Hash,["document"]=r.Document}))};
        var path=directory+"/r8-dashboard-cleanup-"+(apply?"backup-":"audit-")+stamp+".json";
        var bytes=Encoding.UTF8.GetBytes(plan.ToJson(new JsonWriterSettings{Indent=true,OutputMode=JsonOutputMode.RelaxedExtendedJson}));
        await File.WriteAllBytesAsync(path,bytes,ct);await File.WriteAllTextAsync(path+".sha256",Convert.ToHexString(SHA256.HashData(bytes)),ct);
        Console.WriteLine((apply?"BACKUP ":"AUDIT ")+path);Console.WriteLine("COUNTS "+counts.ToJson()+" outsideReferences=0");
        foreach(var row in captured.Where(r=>r.Collection==workName||r.Collection==Name(db.WorkTemplateAssignees)||r.Collection==Name(db.WorkReportPeriods)))Console.WriteLine("SCOPED "+row.Collection+" "+row.Document["_id"]);
        if(!apply)return;
        // Capture the repaired run as a protected control, including its users/units and valid Form.
        var protectedRows=new List<Row>();var protectedFilter=new BsonDocument("note","uat-dashboard-20261005-ff133540");
        foreach(var name in baseNames)foreach(var row in await db.Db.GetCollection<BsonDocument>(name).Find(protectedFilter).ToListAsync(ct))protectedRows.Add(new(name,row,Hash(row)));
        using var session=await db.Db.Client.StartSessionAsync(cancellationToken:ct);
        await session.WithTransactionAsync(async(s,cancel)=>{
            foreach(var name in baseNames){var current=await db.Db.GetCollection<BsonDocument>(name).Find(s,noteFilter).ToListAsync(cancel);var before=captured.Where(r=>r.Collection==name).ToArray();if(current.Count!=before.Length||current.Any(r=>!before.Any(b=>b.Document["_id"]==r["_id"]&&b.Hash==Hash(r))))throw new Exception("Fixture changed after backup; rollback.");}
            await CheckDependencies(s);
            foreach(var row in captured){var filter=new BsonDocument("$and",new BsonArray(row.Document.Elements.Select(e=>new BsonDocument(e.Name,e.Value))));var deleted=await db.Db.GetCollection<BsonDocument>(row.Collection).DeleteOneAsync(s,filter,cancellationToken:cancel);if(deleted.DeletedCount!=1)throw new Exception("Full-document CAS failed; rollback.");}
            return true;
        },new TransactionOptions(readConcern:ReadConcern.Snapshot,writeConcern:WriteConcern.WMajority),ct);
        foreach(var name in baseNames)if(await db.Db.GetCollection<BsonDocument>(name).Find(noteFilter).AnyAsync(ct))throw new Exception("Fixture residual after commit.");
        foreach(var row in protectedRows){var current=await db.Db.GetCollection<BsonDocument>(row.Collection).Find(new BsonDocument("_id",row.Document["_id"])).SingleAsync(ct);if(Hash(current)!=row.Hash)throw new Exception("Protected repaired fixture changed.");}
        await CheckDependencies(null);
        Console.WriteLine("PASS removed "+captured.Count+" exact fixture documents; repaired run unchanged ("+protectedRows.Count+" protected documents); no outside references.");
        await FixtureProvenanceChecks.Migration(db,"after scoped Dashboard fixture cleanup",ct);
        Console.WriteLine("PASS normal provenance migration; no guard/skip switch changed; no host restarted.");
    }
}
