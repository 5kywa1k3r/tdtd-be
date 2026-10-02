using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson.Serialization;
using tdtd_be.Models;
using tdtd_be.Data.Indexes;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class P05ProvenanceRepair
{
    internal const string Audit = "../outputs/provenance-audit-20260929/mismatches.json";
    private sealed record Pin(string Collection, BsonDocument Before, BsonDocument Form, string Hash, string? Fields, string? Tables);
    private static BsonDocument Exact(BsonDocument document) => new() { ["_id"] = document["_id"],
        ["$expr"] = new BsonDocument("$eq", new BsonArray { "$$ROOT", new BsonDocument("$literal", document) }) };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static async Task Repair(bool apply)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)); var ct = timeout.Token;
        var db = Context(); var audit = BsonSerializer.Deserialize<BsonArray>(File.ReadAllText(Audit));
        Require(audit.Count == 90 && audit.Count(x => x["collection"] == "work_assignments") == 88, "Unexpected allowlist");
        var plans = new List<Pin>(); var backup = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        void Keep(string collection, BsonDocument row) => backup.TryAdd(collection + "/" + row["_id"], new BsonDocument { ["collection"] = collection, ["document"] = row });
        foreach(var finding in audit)
        {
            var name=finding["collection"].AsString; Require(name is "work_assignments" or "work_assignment_report", "Outside allowlist");
            var recorded=finding["document"].AsBsonDocument;
            var current=await db.Db.GetCollection<BsonDocument>(name).Find(new BsonDocument("_id",recorded["_id"])).SingleAsync(ct);
            foreach(var field in recorded.Elements) Require(current.GetValue(field.Name,BsonNull.Value)==field.Value,"Pin changed since audit: "+recorded["_id"]);
            var form=await db.Db.GetCollection<BsonDocument>(db.Options.DynamicFormTemplateCollection).Find(new BsonDocument("_id",current["dynamicFormTemplateId"])).SingleAsync(ct);
            var typed=BsonSerializer.Deserialize<DynamicFormTemplate>(form);
            Require(typed.Code.StartsWith("p05-20260928T",StringComparison.Ordinal) && typed.PublishedSchemaHash==finding["template"]["publishedSchemaHash"],"Form changed or not P05");
            var schema=DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(typed);
            Require(current["dynamicFormFamilyId"]==form["familyId"] && current["dynamicFormVersionNo"]==form["versionNo"],"Unexpected family/version drift");
            var work=await db.Db.GetCollection<BsonDocument>(db.Works.CollectionNamespace.CollectionName).Find(new BsonDocument("_id",current["workId"])).SingleAsync(ct);
            Require(work.GetValue("name","").AsString.StartsWith("p05-20260928T",StringComparison.Ordinal),"Not a P05 work");
            Keep(name,current); Keep(db.Options.DynamicFormTemplateCollection,form); Keep(db.Works.CollectionNamespace.CollectionName,work);
            string? fields=null,tables=null;
            if(name=="work_assignment_report")
            {
                var report=BsonSerializer.Deserialize<WorkAssignmentReport>(current);
                Require(report.Id is "6aba67ed666fd163396d79e6" or "6aba77f3be42122f5d2cc065","Unexpected report");
                var old=await new WorkReportPayloadService(db).LoadReportPayloadAsync(report,ct);
                Require(report.PayloadRevision==1 && report.LifecycleRevision==1 && report.Status==tdtd_be.Models.Enums.WorkAssignmentReportStatus.Approved
                    && old.PayloadHashVerified && old.FieldValuesJson=="{\"n\":30}" && string.IsNullOrWhiteSpace(old.TableValuesJson),"Report no longer untouched aborted seed");
                foreach(var collection in new[]{db.WorkReportPayloads.CollectionNamespace.CollectionName,db.WorkReportTableValues.CollectionNamespace.CollectionName})
                    foreach(var row in await db.Db.GetCollection<BsonDocument>(collection).Find(new BsonDocument("reportId",ObjectId.Parse(report.Id))).ToListAsync(ct))Keep(collection,row);
                // No calculation may already depend on either aborted fixture.
                foreach(var collection in AggregateCollections.All)
                {
                    if(collection==AggregateCollections.Declarations)continue;
                    Require(await db.Db.GetCollection<BsonDocument>(collection).CountDocumentsAsync(new BsonDocument("workId",report.WorkId),cancellationToken:ct)==0,"Aggregate consumers exist: "+report.Id);
                }
                if(report.Id=="6aba67ed666fd163396d79e6")
                    fields=JsonSerializer.Serialize(new{n=new string('x',16383)+"📝\n"+string.Concat(Enumerable.Repeat("Nội dung rất dài, tiếng Việt.\n",2000))});
                else
                {
                    var values=Enumerable.Range(1,36).ToDictionary(i=>"n"+i,i=>(object)i);
                    foreach(var i in Enumerable.Range(1,4))values["text"+i]=WideContentChecks.Text(1,i);
                    fields=JsonSerializer.Serialize(values);
                    tables=JsonSerializer.Serialize(new{nativeTables=new{version=1,schemaHash=schema.Sha256,tables=new[]{new{tableId="table",
                        rows=Enumerable.Range(1,16).Select(r=>new{rowId="r"+r,cells=Enumerable.Range(1,10).ToDictionary(c=>"c"+c,c=>new{type="number",state="value",value=40+(r-1)*10+c})})}}}});
                }
                ValidatePayload(typed,fields,tables);
                Console.WriteLine($"VERIFIED aborted report {report.Id}: old numeric n=30 incompatible; reconstruct exact source ordinal 1; fields={JsonDocument.Parse(fields).RootElement.EnumerateObject().Count()}");
            }
            plans.Add(new(name,current,form,schema.Sha256,fields,tables));
        }
        Require(plans.Select(p=>p.Collection+"/"+p.Before["_id"]).Distinct().Count()==90,"Duplicate allowlist");
        var dir=Path.GetFullPath("../outputs/p05-provenance-repair-20260929/"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")+(apply?"-apply":"-rehearsal"));
        Directory.CreateDirectory(dir);
        var bytes=Encoding.UTF8.GetBytes(new BsonArray(backup.Values).ToJson(new MongoDB.Bson.IO.JsonWriterSettings{OutputMode=MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson}));
        using(var file=new FileStream(Path.Combine(dir,"before.ejson"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)){file.Write(bytes);file.Flush(true);}
        File.WriteAllText(Path.Combine(dir,"before.sha256"),Convert.ToHexString(SHA256.HashData(bytes)));
        File.WriteAllText(Path.Combine(dir,"allowlist.json"),new BsonArray(plans.Select(p=>new BsonDocument{["collection"]=p.Collection,["id"]=p.Before["_id"],["oldHash"]=p.Before["dynamicFormSchemaHash"],["newHash"]=p.Hash,["rebuildPayload"]=p.Fields!=null})).ToJson());
        // All existing documents outside exact mutation targets are fingerprinted;
        // concurrent changes are reported, never restored from an old backup.
        var untouched=await Fingerprints(db,plans,ct);
        File.WriteAllText(Path.Combine(dir,"untouched-before.json"),JsonSerializer.Serialize(untouched));
        using var session=await db.Db.Client.StartSessionAsync(cancellationToken:ct);
        session.StartTransaction(new TransactionOptions(readConcern:ReadConcern.Snapshot,writeConcern:WriteConcern.WMajority));
        try
        {
            foreach(var item in backup.Values)
            {
                var collection=db.Db.GetCollection<BsonDocument>(item["collection"].AsString);var row=item["document"].AsBsonDocument;
                Require(await collection.CountDocumentsAsync(session,Exact(row),cancellationToken:ct)==1,"Concurrent data change; abort "+row["_id"]);
            }
            foreach(var plan in plans)
            {
                // Re-check immutable template against the backup with a no-op CAS.
                var fenced=await db.Db.GetCollection<BsonDocument>(db.Options.DynamicFormTemplateCollection).UpdateOneAsync(session,Exact(plan.Form),
                    new BsonDocument("$set",new BsonDocument("publishedSchemaHash",plan.Form["publishedSchemaHash"])),cancellationToken:ct);
                Require(fenced.MatchedCount==1,"Form CAS conflict");
                var set=new BsonDocument("dynamicFormSchemaHash",plan.Hash);
                if(plan.Fields!=null)
                {
                    var report=BsonSerializer.Deserialize<WorkAssignmentReport>(plan.Before);report.DynamicFormSchemaHash=plan.Hash;
                    var saved=await new WorkReportPayloadService(db).SaveReportPayloadAsync(report,"[]",plan.Fields,plan.Tables,null,report.CreatedByUserId,DateTime.UtcNow,ct,session);
                    set["payloadRevision"]=saved.PayloadRevision;set["payloadHash"]=saved.PayloadHash;set["payloadSizeBytes"]=saved.PayloadSizeBytes;set["payloadStatus"]=saved.PayloadStatus;
                }
                var changed=await db.Db.GetCollection<BsonDocument>(plan.Collection).UpdateOneAsync(session,Exact(plan.Before),new BsonDocument("$set",set),cancellationToken:ct);
                Require(changed.MatchedCount==1 && changed.ModifiedCount==1,"Document CAS conflict "+plan.Before["_id"]);
                if(!apply && ReferenceEquals(plan,plans[0]))
                {
                    var stale=await db.Db.GetCollection<BsonDocument>(plan.Collection).UpdateOneAsync(session,Exact(plan.Before),
                        new BsonDocument("$set",new BsonDocument("dynamicFormSchemaHash","must-not-write")),cancellationToken:ct);
                    Require(stale.MatchedCount==0,"Stale CAS unexpectedly accepted");
                    Console.WriteLine("PASS rehearsal stale full-document CAS rejected without overwrite");
                }
            }
            if(apply){await session.CommitTransactionAsync(ct);File.WriteAllText(Path.Combine(dir,"commit.json"),"{\"committed\":true}");}else await session.AbortTransactionAsync(ct);
        }
        catch{if(session.IsInTransaction)await session.AbortTransactionAsync(CancellationToken.None);throw;}
        foreach(var plan in plans)
        {
            var actual=await db.Db.GetCollection<BsonDocument>(plan.Collection).Find(new BsonDocument("_id",plan.Before["_id"])).SingleAsync(ct);
            if(!apply)Require(actual.Equals(plan.Before),"Rehearsal rollback differs");
            else
            {
                var expected=plan.Before.DeepClone().AsBsonDocument;expected["dynamicFormSchemaHash"]=plan.Hash;
                if(plan.Fields!=null)
                {
                    var report=BsonSerializer.Deserialize<WorkAssignmentReport>(actual);var payload=await new WorkReportPayloadService(db).LoadReportPayloadAsync(report,ct);
                    WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report,payload);
                    Require(payload.PayloadHashVerified&&JsonEqual(payload.FieldValuesJson,plan.Fields)&&JsonEqual(payload.TableValuesJson,plan.Tables)&&report.PayloadRevision==2,"Repaired payload readback differs");
                    ValidatePayload(BsonSerializer.Deserialize<DynamicFormTemplate>(plan.Form),payload.FieldValuesJson!,payload.TableValuesJson);
                    foreach(var key in new[]{"payloadRevision","payloadHash","payloadSizeBytes","payloadStatus"})expected[key]=actual[key];
                }
                Require(actual.Equals(expected),"Unexpected changed fields "+actual["_id"]);
            }
        }
        if(!apply)foreach(var item in backup.Values){var actual=await db.Db.GetCollection<BsonDocument>(item["collection"].AsString).Find(new BsonDocument("_id",item["document"]["_id"])).SingleAsync(ct);Require(actual.Equals(item["document"].AsBsonDocument),"Backup rollback mismatch");}
        var after=await Fingerprints(db,plans,ct);var changes=untouched.Keys.Union(after.Keys).Where(k=>!untouched.TryGetValue(k,out var a)||!after.TryGetValue(k,out var b)||a!=b).ToArray();
        File.WriteAllText(Path.Combine(dir,"untouched-differences.json"),JsonSerializer.Serialize(changes));
        Require(changes.Length==0,"Untouched records changed concurrently; inspect evidence without overwriting");
        if(apply)await FixtureProvenanceChecks.Migration(db,"after exact 90-pin repair",ct);
        File.WriteAllText(Path.Combine(dir,"result.json"),JsonSerializer.Serialize(new{apply,assignments=88,reports=2,untouchedRecords=untouched.Count,untouchedDifferences=changes.Length,success=true}));
        Console.WriteLine($"PASS {(apply?"committed":"rolled back rehearsal")} 88 assignment pins + 2 rebuilt report payloads; {untouched.Count} other records unchanged; backup={dir}");
    }
    private static void ValidatePayload(DynamicFormTemplate form,string fields,string? tables)
    {
        DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(form);
        DynamicFormNativeTableValues.Validate(form,form.PublishedSchemaHash,tables,false);
        var fieldIds=JsonDocument.Parse(form.FieldsJson).RootElement.EnumerateArray().Select(x=>x.GetProperty("id").GetString()).ToHashSet();
        Require(JsonDocument.Parse(fields).RootElement.EnumerateObject().All(p=>fieldIds.Contains(p.Name)),"Payload contains unknown fields");
        var snapshot=new WorkReportPayloadSnapshot("[]",fields,tables,null,1,null,Encoding.UTF8.GetByteCount(fields)+(tables?.Length??0),"READY",false,true);
        var values=AggregateNativePayloadAdapter.Read(form,snapshot,AggregateNativePayloadAdapter.Schema(form),null);
        Require(values.Values.All(v=>v.State=="VALUE"),"Payload has invalid or missing fixture values");
    }
    private static bool JsonEqual(string? a,string? b) => System.Text.Json.Nodes.JsonNode.DeepEquals(
        string.IsNullOrWhiteSpace(a)?null:System.Text.Json.Nodes.JsonNode.Parse(a),string.IsNullOrWhiteSpace(b)?null:System.Text.Json.Nodes.JsonNode.Parse(b));
    internal static async Task VerifyRepair(bool afterRegression = false)
    {
        var db=Context();using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));var ct=timeout.Token;
        var dir=Path.GetFullPath("../outputs/p05-provenance-repair-20260929/20260929T005147137-apply");
        var bytes=File.ReadAllBytes(Path.Combine(dir,"before.ejson"));
        Require(Convert.ToHexString(SHA256.HashData(bytes))==File.ReadAllText(Path.Combine(dir,"before.sha256")),"Backup hash mismatch");
        var backup=BsonSerializer.Deserialize<BsonArray>(Encoding.UTF8.GetString(bytes));
        var allow=BsonSerializer.Deserialize<BsonArray>(File.ReadAllText(Path.Combine(dir,"allowlist.json")));
        var plans=new List<Pin>();var details=new BsonArray();var afterDocuments=new BsonArray();
        foreach(var pin in allow)
        {
            var name=pin["collection"].AsString;var id=pin["id"];
            var before=backup.Single(x=>x["collection"]==name&&x["document"]["_id"]==id)["document"].AsBsonDocument;
            var form=backup.Single(x=>x["collection"]==db.Options.DynamicFormTemplateCollection&&x["document"]["_id"]==before["dynamicFormTemplateId"])["document"].AsBsonDocument;
            var actual=await db.Db.GetCollection<BsonDocument>(name).Find(new BsonDocument("_id",id)).SingleAsync(ct);
            afterDocuments.Add(new BsonDocument{["collection"]=name,["document"]=actual});
            var expected=before.DeepClone().AsBsonDocument;expected["dynamicFormSchemaHash"]=pin["newHash"];
            var detail=new BsonDocument{["collection"]=name,["id"]=id,["oldHash"]=pin["oldHash"],["newHash"]=pin["newHash"]};
            if(name=="work_assignment_report")
            {
                var report=BsonSerializer.Deserialize<WorkAssignmentReport>(actual);var typed=BsonSerializer.Deserialize<DynamicFormTemplate>(form);
                var payload=await new WorkReportPayloadService(db).LoadReportPayloadAsync(report,ct);
                WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report,payload);
                ValidatePayload(typed,payload.FieldValuesJson!,payload.TableValuesJson);
                Require(report.PayloadRevision==2&&payload.PayloadHashVerified,"Payload revision/hash");
                using var fields=JsonDocument.Parse(payload.FieldValuesJson!);
                if(report.Id=="6aba67ed666fd163396d79e6")
                {
                    Require(fields.RootElement.EnumerateObject().Count()==1&&fields.RootElement.GetProperty("n").GetString()==new string('x',16383)+"📝\n"+string.Concat(Enumerable.Repeat("Nội dung rất dài, tiếng Việt.\n",2000)),"Exact content text mismatch");
                    Require(string.IsNullOrWhiteSpace(payload.TableValuesJson),"Unexpected content source table");
                }
                else
                {
                    Require(fields.RootElement.EnumerateObject().Count()==40,"Mixed field count");
                    foreach(var i in Enumerable.Range(1,36))Require(fields.RootElement.GetProperty("n"+i).GetInt32()==i,"Mixed scalar mismatch");
                    foreach(var i in Enumerable.Range(1,4))Require(fields.RootElement.GetProperty("text"+i).GetString()==WideContentChecks.Text(1,i),"Mixed exact Unicode mismatch");
                    using var tables=JsonDocument.Parse(payload.TableValuesJson!);var values=tables.RootElement.GetProperty("nativeTables").GetProperty("tables");
                    Require(values.GetArrayLength()==1&&values[0].GetProperty("tableId").GetString()=="table","Mixed table identity");
                    var rows=values[0].GetProperty("rows");Require(rows.GetArrayLength()==16,"Mixed rows");
                    foreach(var r in Enumerable.Range(1,16))
                    {
                        var row=rows[r-1];Require(row.GetProperty("rowId").GetString()=="r"+r&&row.GetProperty("cells").EnumerateObject().Count()==10,"Mixed row structure");
                        foreach(var c in Enumerable.Range(1,10))Require(row.GetProperty("cells").GetProperty("c"+c).GetProperty("value").GetInt32()==40+(r-1)*10+c,"Mixed cell value");
                    }
                }
                foreach(var key in new[]{"payloadRevision","payloadHash","payloadSizeBytes","payloadStatus"})expected[key]=actual[key];
                detail["payloadRevision"]=report.PayloadRevision;detail["payloadHashVerified"]=true;detail["exactFixtureValuesVerified"]=true;
            }
            Require(actual.Equals(expected),"Unexpected record fields "+id);
            plans.Add(new(name,before,form,pin["newHash"].AsString,name=="work_assignment_report"?"verified":null,null));details.Add(detail);
        }
        var beforeHashes=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(dir,"untouched-before.json")))!;
        var afterHashes=await Fingerprints(db,plans,ct);
        var keys=afterRegression?beforeHashes.Keys:beforeHashes.Keys.Union(afterHashes.Keys);
        var differences=keys.Where(k=>!beforeHashes.TryGetValue(k,out var a)||!afterHashes.TryGetValue(k,out var b)||a!=b).ToArray();
        File.WriteAllText(Path.Combine(dir,afterRegression?"existing-record-differences-final.json":"untouched-differences.json"),JsonSerializer.Serialize(differences));
        if(afterRegression)File.WriteAllText(Path.Combine(dir,"new-record-keys-after-regression.json"),JsonSerializer.Serialize(afterHashes.Keys.Except(beforeHashes.Keys).OrderBy(k=>k)));
        Require(differences.Length==0,"Untouched differences; do not overwrite");
        await FixtureProvenanceChecks.Migration(db,"repaired database",ct);
        foreach(var collection in new[]{db.WorkReportPayloads.CollectionNamespace.CollectionName,db.WorkReportTableValues.CollectionNamespace.CollectionName})
            foreach(var row in await db.Db.GetCollection<BsonDocument>(collection).Find(new BsonDocument("reportId",new BsonDocument("$in",new BsonArray(plans.Where(p=>p.Fields!=null).Select(p=>p.Before["_id"]))))).ToListAsync(ct))
                afterDocuments.Add(new BsonDocument{["collection"]=collection,["document"]=row});
        var afterBytes=Encoding.UTF8.GetBytes(afterDocuments.ToJson(new MongoDB.Bson.IO.JsonWriterSettings{OutputMode=MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson}));
        File.WriteAllBytes(Path.Combine(dir,"after.ejson"),afterBytes);File.WriteAllText(Path.Combine(dir,"after.sha256"),Convert.ToHexString(SHA256.HashData(afterBytes)));
        File.WriteAllText(Path.Combine(dir,"verified-ids.json"),details.ToJson());
        File.WriteAllText(Path.Combine(dir,afterRegression?"result-after-regression.json":"result.json"),JsonSerializer.Serialize(new{committed=true,verified=true,assignments=88,reports=2,untouchedRecords=beforeHashes.Count,untouchedDifferences=0,newRecords=afterHashes.Count-beforeHashes.Count}));
        Console.WriteLine($"PASS repair readback: 88 assignment pins, 2 report payloads exact text/36 numbers/160 cells; {beforeHashes.Count} other records unchanged; migration PASS; evidence={dir}");
    }
    private static async Task<Dictionary<string,string>> Fingerprints(MongoDbContext db,List<Pin> plans,CancellationToken ct)
    {
        var result=new Dictionary<string,string>();var targets=plans.Select(p=>p.Collection+"/"+p.Before["_id"]).ToHashSet();
        var reportIds=plans.Where(p=>p.Fields!=null).Select(p=>p.Before["_id"]).ToHashSet();
        var names=DynamicFormRuntimeProvenanceBackfill.GetRuntimeCollectionNames(db.Options).Concat(new[]{db.Options.DynamicFormTemplateCollection,
            db.Works.CollectionNamespace.CollectionName,db.WorkReportPayloads.CollectionNamespace.CollectionName,db.WorkReportTableValues.CollectionNamespace.CollectionName}).Distinct();
        foreach(var name in names)
        {
            using var cursor=await db.Db.GetCollection<BsonDocument>(name).FindAsync(new BsonDocument(),cancellationToken:ct);
            while(await cursor.MoveNextAsync(ct))foreach(var row in cursor.Current)
            {
                if(targets.Contains(name+"/"+row["_id"]))continue;
                if((name==db.WorkReportPayloads.CollectionNamespace.CollectionName||name==db.WorkReportTableValues.CollectionNamespace.CollectionName)&&reportIds.Contains(row.GetValue("reportId",BsonNull.Value)))continue;
                result[name+"/"+row["_id"]]=Convert.ToHexString(SHA256.HashData(row.ToBson()));
            }
        }
        return result;
    }
    internal static MongoDbContext Context() => new(Options.Create(new MongoOptions {
        ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs", Database = "tdtd" }));
    internal static async Task Inspect()
    {
        var db = Context(); var output = Path.GetFullPath("../outputs/p05-provenance-repair-20260929");
        Directory.CreateDirectory(output);
        var list = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonArray>(File.ReadAllText("../outputs/provenance-audit-20260929/mismatches.json"));
        foreach(var item in list.Where(x => x["collection"] == "work_assignment_report"))
        {
            var id = item["document"]["_id"].ToString()!;
            var report = await db.WorkAssignmentReports.Find(x=>x.Id==id).SingleAsync();
            var form = await db.DynamicFormTemplates.Find(x=>x.Id==report.DynamicFormTemplateId).SingleAsync();
            var payload = await new WorkReportPayloadService(db).LoadReportPayloadAsync(report);
            File.WriteAllText(Path.Combine(output,id+"-inspect.json"),System.Text.Json.JsonSerializer.Serialize(new {report,form,payload}));
            Console.WriteLine(new BsonDocument { ["reportId"]=id,["status"]=report.Status.ToString(),["revision"]=report.PayloadRevision,
                ["fieldsLength"]=payload.FieldValuesJson?.Length??0,["tableLength"]=payload.TableValuesJson?.Length??0,
                ["tableSample"]=(payload.TableValuesJson??"")[..Math.Min(100,payload.TableValuesJson?.Length??0)],
                ["payloadVerified"]=payload.PayloadHashVerified,["fieldCount"]=JsonDocument.Parse(payload.FieldValuesJson??"{}").RootElement.EnumerateObject().Count() }.ToJson());
        }
    }
}
