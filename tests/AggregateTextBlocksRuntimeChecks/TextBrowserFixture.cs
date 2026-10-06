using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Auth;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

// Own synthetic R8 fixtures only. Publish new Forms before pins, prepare existing
// authorization projections and repair missing empty statistic metadata with CAS.
// Published structure, runtime pins, report payloads and acceptance data stay intact.
internal static class TextBrowserFixture
{
    internal static async Task RepairEmptyStatisticConfig(MongoDbContext db,string workId,CancellationToken ct)
    {
        await FixtureProvenanceChecks.Migration(db,"R8 before own empty statistic fixture repair",ct);
        var work=await db.Works.Find(w=>w.Id==workId&&!w.IsDeleted).SingleAsync(ct);
        var actor=await db.Users.Find(u=>u.Id==work.CreatedByUserId&&!u.IsDeleted).SingleAsync(ct);
        if(!work.AutoCode.StartsWith("r8-text-",StringComparison.Ordinal)||!actor.Username.StartsWith(work.AutoCode+"-",StringComparison.Ordinal))
            throw new InvalidOperationException("Only this harness's own R8 fixture may be repaired.");
        var formIds=await db.WorkAssignments.Find(a=>a.WorkId==workId&&!a.IsDeleted).Project(a=>a.DynamicFormTemplateId).ToListAsync(ct);
        var collection=db.Db.GetCollection<BsonDocument>(db.DynamicFormTemplates.CollectionNamespace.CollectionName);
        var expectedForms=formIds.Where(id=>id!=null).Distinct().Select(id=>ObjectId.Parse(id!)).ToArray();
        var originals=await collection.Find(new BsonDocument("_id",new BsonDocument("$in",new BsonArray(expectedForms)))).ToListAsync(ct);
        if(originals.Count!=expectedForms.Length||originals.Count==0)throw new InvalidOperationException("Fixture Form inventory incomplete; no repair.");
        var backupPath=$"../outputs/aggregate-operators-20261005/r8-own-form-stat-backup-{DateTime.UtcNow:yyyyMMddTHHmmssfff}.json";
        var backup=new BsonDocument{["workId"]=workId,["forms"]=new BsonArray(originals)}.ToJson(new(){Indent=true});
        await File.WriteAllTextAsync(backupPath,backup,ct);
        await File.WriteAllTextAsync(backupPath+".sha256",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(backup))),ct);
        var http=new DefaultHttpContext();http.Items[MeAccessor.MeItemKey]=new MeResponse(actor.Id,actor.Username,"R8 fixture",[],actor.UnitId!,null,null,null,[],null,false);
        var me=new MeAccessor(new HttpContextAccessor{HttpContext=http});
        var config=new DynamicFormStatisticConfigCommandService(db,me,new StatConfigTransactionRunner(db,NullLogger<StatConfigTransactionRunner>.Instance));
        using var session=await db.Db.Client.StartSessionAsync(cancellationToken:ct);
        session.StartTransaction(new(readConcern:ReadConcern.Snapshot,writeConcern:WriteConcern.WMajority));
        try{
            foreach(var original in originals){
                var owner=BsonSerializer.Deserialize<DynamicFormTemplate>(original);
                if(owner.NativeTablesVersion==null)continue;
                if(owner.CreatedByUserId!=actor.Id||!owner.IsPublished||!owner.Code.StartsWith(work.AutoCode,StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected Form owner/publication; no repair.");
                if(owner.StatisticConfigId!=null||owner.StatisticConfigRevision!=0){
                    _=DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(owner,owner.StatisticConfigId!,owner.StatisticConfigVersionId!,owner.StatisticConfigVersionNo,owner.StatisticConfigRevision,owner.StatisticConfigHash!);continue;
                }
                var state=await config.GetAsync(owner.Id,ct);
                if(state.Status!="LOCKED"||state.VersionNo!=1||state.Revision!=1||state.Fields.Any(f=>f.IsStatistic)||state.TableConfig.Count!=0||state.NativeTargetConfig is not {Count:0})
                    throw new InvalidOperationException("Only a trusted empty initial statistic config may be materialized.");
                var sections=new DynamicFormStatisticConfigSections{
                    FieldSectionJson=StatConfigCanonicalJson.Canonicalize(state.Fields),TableSectionJson=StatConfigCanonicalJson.Canonicalize(state.TableConfig),
                    NativeTargetSectionJson=StatConfigCanonicalJson.Canonicalize(state.NativeTargetConfig),
                    NativePlanSectionJson=state.NativePlanConfig==null?null:StatConfigCanonicalJson.Canonicalize(state.NativePlanConfig)};
                owner.StatisticConfigId=state.ConfigId;owner.StatisticConfigVersionId=state.VersionId;owner.StatisticConfigPreviousVersionId=null;
                owner.StatisticConfigVersionNo=state.VersionNo;owner.StatisticConfigRevision=state.Revision;owner.StatisticConfigHash=state.ConfigHash;
                owner.StatisticConfigStatus=state.Status;owner.StatisticConfigDependencyPins=state.DependencyPins.ToList();owner.StatisticConfigSections=sections;
                var now=DateTime.UtcNow;owner.StatisticConfigUpdatedAtUtc=now;owner.StatisticConfigUpdatedByUserId=actor.Id;
                owner.StatisticConfigSnapshots=[new(){VersionId=state.VersionId,VersionNo=state.VersionNo,Revision=state.Revision,Status=state.Status,
                    ConfigHash=state.ConfigHash,DependencyPins=state.DependencyPins.ToList(),Sections=sections,CreatedAtUtc=now,CreatedByUserId=actor.Id}];
                _=DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(owner,state.ConfigId,state.VersionId,state.VersionNo,state.Revision,state.ConfigHash);
                var result=await collection.ReplaceOneAsync(session,original,owner.ToBsonDocument(),cancellationToken:ct);
                if(result.ModifiedCount!=1)throw new InvalidOperationException("Fixture changed concurrently; transaction rolled back.");
                Console.WriteLine($"STAGED own fixture empty statistic config: form={owner.Id} schemaHash={owner.PublishedSchemaHash}; structure/pins/reports unchanged.");
            }
            await session.CommitTransactionAsync(ct);
        }catch{await session.AbortTransactionAsync(ct);throw;}
        foreach(var original in originals){
            var after=await collection.Find(new BsonDocument("_id",original["_id"])).SingleAsync(ct);
            var beforeStructure=new BsonDocument(original.Where(e=>!e.Name.StartsWith("statisticConfig",StringComparison.OrdinalIgnoreCase)));
            var afterStructure=new BsonDocument(after.Where(e=>!e.Name.StartsWith("statisticConfig",StringComparison.OrdinalIgnoreCase)));
            if(!beforeStructure.Equals(afterStructure))throw new InvalidOperationException("Unexpected non-statistic Form change after repair.");
            var owner=BsonSerializer.Deserialize<DynamicFormTemplate>(after);
            if(owner.NativeTablesVersion!=null)
                _=DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(owner,owner.StatisticConfigId!,owner.StatisticConfigVersionId!,owner.StatisticConfigVersionNo,owner.StatisticConfigRevision,owner.StatisticConfigHash!);
            Console.WriteLine($"PASS fixture Form readback preserves every non-statistic field: {owner.Id}");
        }
        await FixtureProvenanceChecks.Migration(db,"R8 after own empty statistic fixture repair",ct);
        Console.WriteLine($"PASS own empty fixture statistic metadata repaired via trusted read + validation + full-document CAS/transaction; backup={backupPath}");
    }
    internal static async Task<DynamicFormTemplate> Publish(MongoDbContext db,DynamicFormTemplate form,CancellationToken ct)
    {
        if(form.IsPublished||!form.Code.StartsWith("r8-text-",StringComparison.Ordinal))
            throw new InvalidOperationException("Only a new R8 draft fixture may be published.");
        var actor=await db.Users.Find(u=>u.Id==form.CreatedByUserId).SingleAsync(ct);
        var http=new DefaultHttpContext();
        http.Items[MeAccessor.MeItemKey]=new MeResponse(actor.Id,actor.Username,"Kiểm thử R8 — dữ liệu giả",[],actor.UnitId!,null,null,null,[],null,false);
        var me=new MeAccessor(new HttpContextAccessor{HttpContext=http});
        var config=new DynamicFormStatisticConfigCommandService(db,me,new StatConfigTransactionRunner(db,NullLogger<StatConfigTransactionRunner>.Instance));
        var service=new DynamicFormService(db,me,config,new LabelEnumCatalogService(db,me));
        await service.PublishAsync(form.Id,new(form.Revision),ct);
        var published=await db.DynamicFormTemplates.Find(t=>t.Id==form.Id).SingleAsync(ct);
        if(published.NativeTablesVersion!=null)
            _=DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(published,published.StatisticConfigId!,published.StatisticConfigVersionId!,published.StatisticConfigVersionNo,published.StatisticConfigRevision,published.StatisticConfigHash!);
        Console.WriteLine($"PASS R8 normal Form publication before assignment/report pins: {published.Id}");
        return published;
    }
    internal static async Task Prepare(MongoDbContext db,string workId,CancellationToken ct)
    {
        await FixtureProvenanceChecks.Migration(db,"R8 before browser preparation",ct);
        var work=await db.Works.Find(w=>w.Id==workId&&!w.IsDeleted).SingleAsync(ct);
        var actor=await db.Users.Find(u=>u.Id==work.CreatedByUserId&&!u.IsDeleted).SingleAsync(ct);
        if(!work.AutoCode.StartsWith("r8-text-",StringComparison.Ordinal)||!actor.Username.StartsWith(work.AutoCode+"-",StringComparison.Ordinal))
            throw new InvalidOperationException("Only this harness's synthetic R8 owner may be prepared.");
        await NormalizeOwnPeriodicType(db,work,actor.Id,ct);
        if(string.IsNullOrEmpty(actor.PasswordHash)){
            var password=Environment.GetEnvironmentVariable("AGG_FIXTURE_PASSWORD")??throw new InvalidOperationException("Supply the authorized test credential in process environment; never print it.");
            var previousUsername=actor.Username;
            actor.Username=actor.Username.ToLowerInvariant();actor.FullName="Kiểm thử R8 — dữ liệu giả";
            actor.PasswordHash=new PasswordHasher<AppUser>().HashPassword(actor,password);
            var saved=await db.Users.ReplaceOneAsync(u=>u.Id==actor.Id&&u.Username==previousUsername&&u.PasswordHash==null,actor,cancellationToken:ct);
            if(saved.ModifiedCount!=1)throw new InvalidOperationException("Fixture credential changed concurrently; no overwrite.");
        }
        var projection=new DocRoleReadModelProjectionService(db);var roles=new DocRoleService(db,projection);
        await roles.UpsertWorkRootRolesAsync(work,ct);
        foreach(var assignment in await db.WorkAssignments.Find(a=>a.WorkId==workId&&!a.IsDeleted).ToListAsync(ct))
            await roles.UpsertWorkAssignmentRolesAsync(assignment,ct);
        await roles.RebuildWorkParticipantRolesFromAssignmentsAsync(workId,actor.Id,ct);
        await projection.RebuildWorkAssignmentsAsync(workId,actor.Id,ct);
        await projection.RebuildWorkReportPeriodsAsync(workId,actor.Id,ct);
        await FixtureProvenanceChecks.Migration(db,"R8 after browser preparation",ct);
        Console.WriteLine($"R8_BROWSER work={workId} username={actor.Username} actor={actor.Id}; normal product login and authorization, own fixture only.");
    }

    private static async Task NormalizeOwnPeriodicType(MongoDbContext db,Work work,string actorId,CancellationToken ct)
    {
        if(!work.AutoCode.EndsWith("-monthly",StringComparison.Ordinal))return;
        var collections=new[]{db.WorkAssignments.CollectionNamespace.CollectionName,db.WorkTemplateAssignees.CollectionNamespace.CollectionName};
        var originals=new List<(string Collection,BsonDocument Document)>();
        foreach(var name in collections){
            var documents=await db.Db.GetCollection<BsonDocument>(name).Find(new BsonDocument("workId",ObjectId.Parse(work.Id))).ToListAsync(ct);
            if(documents.Count!=2)throw new InvalidOperationException("Own monthly fixture must have exactly two assignments/bindings.");
            foreach(var document in documents){
                var type=document.GetValue("assignmentType",BsonNull.Value);
                if(type=="PERIODIC_REPORT")continue;
                if(type!="PERIODIC"||document.GetValue("createdByUserId",BsonNull.Value)!=ObjectId.Parse(actorId)
                    ||!document.TryGetValue("schedule",out var schedule)||!schedule.IsBsonDocument
                    ||schedule.AsBsonDocument.GetValue("cycleType",BsonNull.Value)!="MONTHLY")
                    throw new InvalidOperationException("Unexpected fixture owner/type/schedule; no repair.");
                originals.Add((name,document));
            }
        }
        if(originals.Count==0)return;
        var backupPath=$"../outputs/aggregate-operators-20261005/p05-own-periodic-type-backup-{work.Id}-{DateTime.UtcNow:yyyyMMddTHHmmssfff}.json";
        await File.WriteAllTextAsync(backupPath,new BsonDocument{["workId"]=work.Id,["documents"]=new BsonArray(originals.Select(x=>new BsonDocument{["collection"]=x.Collection,["before"]=x.Document}))}.ToJson(new(){Indent=true}),ct);
        using var session=await db.Db.Client.StartSessionAsync(cancellationToken:ct);
        session.StartTransaction(new(readConcern:ReadConcern.Snapshot,writeConcern:WriteConcern.WMajority));
        try{
            foreach(var original in originals){
                var result=await db.Db.GetCollection<BsonDocument>(original.Collection).UpdateOneAsync(session,original.Document,
                    new BsonDocument("$set",new BsonDocument("assignmentType","PERIODIC_REPORT")),cancellationToken:ct);
                if(result.ModifiedCount!=1)throw new InvalidOperationException("Own fixture changed concurrently; rollback.");
            }
            await session.CommitTransactionAsync(ct);
        }catch{await session.AbortTransactionAsync(ct);throw;}
        foreach(var original in originals){
            var after=await db.Db.GetCollection<BsonDocument>(original.Collection).Find(new BsonDocument("_id",original.Document["_id"])).SingleAsync(ct);
            var expected=original.Document.DeepClone().AsBsonDocument;expected["assignmentType"]="PERIODIC_REPORT";
            if(!expected.Equals(after))throw new InvalidOperationException("Unexpected non-type fixture change.");
            Console.WriteLine($"PASS own fixture periodic type only: {original.Collection}/{original.Document["_id"]}; full-document CAS/transaction and readback; backup={backupPath}");
        }
    }
}
