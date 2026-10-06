using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

const string workId="6ac3cc49215962add5c1bb75",formId="6ac3cc49215962add5c1bb7b";
if(args.SequenceEqual(new[]{"--database","tdtd","--audit-broken-dashboard-cleanup"})||args.SequenceEqual(new[]{"--database","tdtd","--delete-broken-dashboard-fixtures"})){
    await DashboardFixtureCleanup.Run(args[2]=="--delete-broken-dashboard-fixtures");return;
}
var auditOnly=args.SequenceEqual(new[]{"--database","tdtd","--audit-dashboard-fixture-pins"});
if(!auditOnly&&!args.SequenceEqual(new[]{"--database","tdtd","--repair-authorized-dashboard-fixture"}))throw new ArgumentException("Only the explicitly transferred dashboard fixture is allowed.");
using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));var ct=timeout.Token;
var db=new MongoDbContext(Options.Create(new MongoOptions{ConnectionString="mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000",Database="tdtd"}));
if(auditOnly){
    var candidates=await db.WorkTemplateAssignees.Find(b=>b.Note!=null&&b.Note.StartsWith("uat-dashboard-20261005-")).ToListAsync(ct);
    var rows=new List<BsonDocument>();
    foreach(var group in candidates.GroupBy(b=>b.DynamicFormTemplateId)){
        var template=await db.DynamicFormTemplates.Find(f=>f.Id==group.Key).FirstOrDefaultAsync(ct);
        var sample=group.First();var fixtureWork=await db.Works.Find(w=>w.Id==sample.WorkId).SingleAsync(ct);
        var fixturePeriods=await db.WorkReportPeriods.Find(p=>p.DynamicFormTemplateId==group.Key).ToListAsync(ct);
        var reportCount=await db.WorkAssignmentReports.CountDocumentsAsync(r=>r.WorkId==sample.WorkId||r.DynamicFormTemplateId==group.Key,cancellationToken:ct);
        rows.Add(new BsonDocument{["workId"]=sample.WorkId,["workCode"]=fixtureWork.AutoCode,["formId"]=group.Key,["templateExists"]=template!=null,["bindingIds"]=new BsonArray(group.Select(b=>b.Id)),["periodIds"]=new BsonArray(fixturePeriods.Select(p=>p.Id)),["reportCount"]=reportCount,["pinHashes"]=new BsonArray(group.Select(b=>b.DynamicFormSchemaHash is null?(BsonValue)BsonNull.Value:new BsonString(b.DynamicFormSchemaHash))),["createdAtUtc"]=sample.CreatedAtUtc});
    }
    var audit=new BsonDocument{["mode"]="READ_ONLY",["fixtures"]=new BsonArray(rows)};
    var auditText=audit.ToJson(new JsonWriterSettings{Indent=true,OutputMode=JsonOutputMode.RelaxedExtendedJson});Console.WriteLine(auditText);
    await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/r8-dashboard-pins-audit.json",auditText,ct);return;
}
var work=await db.Works.Find(w=>w.Id==workId).SingleAsync(ct);
if(!work.AutoCode.StartsWith("uat-dashboard-20261005-",StringComparison.Ordinal))throw new Exception("Work is not the expected own dashboard fixture.");
var bindings=await db.WorkTemplateAssignees.Find(b=>b.DynamicFormTemplateId==formId).ToListAsync(ct);
var periods=await db.WorkReportPeriods.Find(p=>p.DynamicFormTemplateId==formId).ToListAsync(ct);
if(bindings.Count!=2||periods.Count!=2||bindings.Any(b=>b.WorkId!=workId)||periods.Any(p=>p.WorkId!=workId)||bindings.Select(b=>b.Note).Distinct().Count()!=1)throw new Exception("Fixture identity/cardinality changed; no write.");
if(await db.WorkAssignmentReports.Find(r=>r.DynamicFormTemplateId==formId||r.WorkId==workId).AnyAsync(ct))throw new Exception("A report exists; payload/schema inspection required, no write.");
var run=bindings[0].Note!;
var existing=await db.DynamicFormTemplates.Find(f=>f.Id==formId).FirstOrDefaultAsync(ct);
if(existing!=null){
    DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(existing);
    if(existing.Note!=run||bindings.Any(b=>b.DynamicFormFamilyId!=existing.FamilyId||b.DynamicFormVersionNo!=existing.VersionNo||b.DynamicFormSchemaHash!=existing.PublishedSchemaHash)
        ||periods.Any(p=>p.DynamicFormFamilyId!=existing.FamilyId||p.DynamicFormVersionNo!=existing.VersionNo||p.DynamicFormSchemaHash!=existing.PublishedSchemaHash))throw new Exception("Existing Form/pins differ, no write.");
    var backupPath="../outputs/aggregate-operators-20261005/r8-dashboard-fixture-backup-20261005T164930.json";
    var backupBytes=await File.ReadAllBytesAsync(backupPath,ct);
    if(Convert.ToHexString(SHA256.HashData(backupBytes))!=(await File.ReadAllTextAsync(backupPath+".sha256",ct)).Trim())throw new Exception("Backup checksum mismatch.");
    var beforeBackup=BsonDocument.Parse(Encoding.UTF8.GetString(backupBytes));
    foreach(var entry in beforeBackup["before"].AsBsonArray.Select(b=>b.AsBsonDocument)){
        var expected=entry["document"].DeepClone().AsBsonDocument;var collection=entry["collection"].AsString;
        if(collection==db.WorkTemplateAssignees.CollectionNamespace.CollectionName||collection==db.WorkReportPeriods.CollectionNamespace.CollectionName){expected["dynamicFormFamilyId"]=ObjectId.Parse(existing.FamilyId!);expected["dynamicFormVersionNo"]=existing.VersionNo;expected["dynamicFormSchemaHash"]=existing.PublishedSchemaHash;}
        var current=await db.Db.GetCollection<BsonDocument>(collection).Find(new BsonDocument("_id",expected["_id"])).SingleAsync(ct);
        if(!current.Equals(expected))throw new Exception("Current fixture differs beyond the four authorized pins.");
    }
    Console.WriteLine("PASS ALREADY_REPAIRED no duplicate Form or writes; two bindings/two periods match immutable Form.");
    Console.WriteLine("PASS backup checksum and full-document comparison: only four pins changed; Work/Assignment unchanged.");
    foreach(var binding in bindings)Console.WriteLine("VERIFIED binding "+binding.Id);foreach(var period in periods)Console.WriteLine("VERIFIED period "+period.Id);
    await FixtureProvenanceChecks.Migration(db,"authorized fixture already repaired",ct);return;
}
if(bindings.Any(b=>b.DynamicFormFamilyId!=null||b.DynamicFormVersionNo!=null||b.DynamicFormSchemaHash!=null)||periods.Any(p=>p.DynamicFormFamilyId!=null||p.DynamicFormVersionNo!=null||p.DynamicFormSchemaHash!=null))throw new Exception("Pins no longer match audited null state; no write.");
IMongoCollection<BsonDocument> Raw<T>(IMongoCollection<T> collection)=>db.Db.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
(IMongoCollection<BsonDocument> Collection,string[] Ids)[] scopes=[(Raw(db.Works),new[]{workId}), (Raw(db.WorkAssignments),bindings.Select(b=>b.WorkAssignmentId).Distinct().ToArray()),
    (Raw(db.WorkTemplateAssignees),bindings.Select(b=>b.Id).ToArray()),(Raw(db.WorkReportPeriods),periods.Select(p=>p.Id).ToArray())];
string Hash(BsonDocument row)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.ToJson())));
var captured=new List<(IMongoCollection<BsonDocument> Collection,BsonDocument Row,string Hash)>();
foreach(var scope in scopes)foreach(var id in scope.Ids){var row=await scope.Collection.Find(new BsonDocument("_id",ObjectId.Parse(id))).SingleAsync(ct);captured.Add((scope.Collection,row,Hash(row)));}
var form=DashboardFixtureForm.Create(formId,run,work.CreatedByUserId!);
var backup=new BsonDocument{["mode"]="AUTHORIZED_FIXTURE_REPAIR",["workId"]=workId,["missingFormId"]=formId,["formPreviouslyAbsent"]=true,["createdAtUtc"]=DateTime.UtcNow,
    ["before"]=new BsonArray(captured.Select(c=>new BsonDocument{["collection"]=c.Collection.CollectionNamespace.CollectionName,["sha256"]=c.Hash,["document"]=c.Row})),["newForm"]=form.ToBsonDocument()};
var directory="../outputs/aggregate-operators-20261005";Directory.CreateDirectory(directory);
var path=directory+"/r8-dashboard-fixture-backup-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmss")+".json";
var backupText=backup.ToJson(new JsonWriterSettings{Indent=true,OutputMode=JsonOutputMode.RelaxedExtendedJson});await File.WriteAllTextAsync(path,backupText,ct);await File.WriteAllTextAsync(path+".sha256",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(backupText))),ct);
Console.WriteLine("BACKUP "+path);
using var session=await db.Db.Client.StartSessionAsync(cancellationToken:ct);
await session.WithTransactionAsync(async(s,cancel)=>{
    foreach(var capture in captured){var current=await capture.Collection.Find(s,new BsonDocument("_id",capture.Row["_id"])).SingleAsync(cancel);if(Hash(current)!=capture.Hash)throw new Exception("Concurrent fixture change; transaction rejected.");}
    if(await db.DynamicFormTemplates.Find(s,f=>f.Id==formId).AnyAsync(cancel)||await db.WorkAssignmentReports.Find(s,r=>r.DynamicFormTemplateId==formId||r.WorkId==workId).AnyAsync(cancel))throw new Exception("Concurrent Form/report appeared; transaction rejected.");
    await db.DynamicFormTemplates.InsertOneAsync(s,form,cancellationToken:cancel);
    foreach(var capture in captured.Where(c=>c.Collection.CollectionNamespace.CollectionName==db.WorkTemplateAssignees.CollectionNamespace.CollectionName||c.Collection.CollectionNamespace.CollectionName==db.WorkReportPeriods.CollectionNamespace.CollectionName)){
        // Match every observed property as well as the transaction's snapshot.
        var filter=new BsonDocument("$and",new BsonArray(capture.Row.Elements.Select(e=>new BsonDocument(e.Name,e.Value))));
        var update=new BsonDocument("$set",new BsonDocument{["dynamicFormFamilyId"]=ObjectId.Parse(form.FamilyId!),["dynamicFormVersionNo"]=form.VersionNo,["dynamicFormSchemaHash"]=form.PublishedSchemaHash});
        var result=await capture.Collection.UpdateOneAsync(s,filter,update,cancellationToken:cancel);if(result.ModifiedCount!=1)throw new Exception("Compare-and-set failed; transaction rolled back.");
    }
    return true;
},new TransactionOptions(readConcern:ReadConcern.Snapshot,writeConcern:WriteConcern.WMajority),ct);
var persisted=await db.DynamicFormTemplates.Find(f=>f.Id==formId).SingleAsync(ct);DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(persisted);
foreach(var capture in captured){var after=await capture.Collection.Find(new BsonDocument("_id",capture.Row["_id"])).SingleAsync(ct);var expected=capture.Row.DeepClone().AsBsonDocument;
    if(capture.Collection.CollectionNamespace.CollectionName==db.WorkTemplateAssignees.CollectionNamespace.CollectionName||capture.Collection.CollectionNamespace.CollectionName==db.WorkReportPeriods.CollectionNamespace.CollectionName){expected["dynamicFormFamilyId"]=ObjectId.Parse(form.FamilyId!);expected["dynamicFormVersionNo"]=form.VersionNo;expected["dynamicFormSchemaHash"]=form.PublishedSchemaHash;}
    if(!after.Equals(expected))throw new Exception("Unexpected post-repair mutation.");}
Console.WriteLine("PASS created valid immutable fixture Form "+formId+" schema="+form.PublishedSchemaHash);
foreach(var binding in bindings)Console.WriteLine("REPAIRED binding "+binding.Id);foreach(var period in periods)Console.WriteLine("REPAIRED period "+period.Id);
Console.WriteLine("PASS only four pins changed; Work/Assignment unchanged; no report payload exists.");
await FixtureProvenanceChecks.Migration(db,"after authorized Dashboard fixture repair",ct);
