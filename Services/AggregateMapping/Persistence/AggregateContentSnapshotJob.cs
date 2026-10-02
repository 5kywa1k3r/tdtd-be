using System.Security.Cryptography;
using System.Text.Json;
using Hangfire;
using Minio;
using Minio.DataModel.Args;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal sealed record AggregateContentSnapshotIntent(string ReportId, string TableId, long PayloadRevision, AggregateContentReference Reference, string? ViewId = null)
{
    internal string TargetId => ViewId ?? ReportId;
}

// A durable post-commit artifact. Failure never rolls back or falsely confirms the saved report.
public sealed class AggregateContentSnapshotJob(MongoDbContext db, IMinioClient minio, IConfiguration configuration)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task Run(string id, CancellationToken ct)
    {
        if (!AggregateIntegrationGate.V2Ready(configuration)) return;
        var collection=db.Db.GetCollection<BsonDocument>(AggregateCollections.ContentSnapshots);
        var row=await collection.Find(new BsonDocument("_id",id)).FirstOrDefaultAsync(ct);
        if(row==null||row.GetValue("state", "PENDING")=="READY")return;
        var intent=AggregateMongoTransaction.Read<AggregateContentSnapshotIntent>(row).Value;
        await using var gate = await AggregateContentScopeGate.Acquire(db, intent.TargetId, ct)
            ?? throw new AggregatePreviewException("AGG_INPUT_STALE");
        // A cleanup may have removed this intent while upload was waiting for its scope.
        row = await collection.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
        if (row == null || row.GetValue("state", "PENDING") == "READY") return;
        var store=new AggregateContentTableStore(db);
        var bucket=configuration["Uploads:Bucket"]??"tdtd-attachments";
        var objectKey=$"aggregate-content/{intent.TargetId}/{intent.PayloadRevision}/{intent.TableId}/{intent.Reference.Hash}.ndjson";
        var path=Path.Combine(Path.GetTempPath(),"tdtd-content-"+Guid.NewGuid().ToString("N")+".tmp");
        await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,65536,FileOptions.Asynchronous|FileOptions.DeleteOnClose);
        await store.WriteSnapshot(intent.TargetId,intent.Reference,file,ct);
        file.Position=0;var hash=Convert.ToHexString(await SHA256.HashDataAsync(file,ct));file.Position=0;
        if(!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket),ct))
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket),ct);
        await minio.PutObjectAsync(new PutObjectArgs().WithBucket(bucket).WithObject(objectKey).WithStreamData(file)
            .WithObjectSize(file.Length).WithContentType("application/x-ndjson"),ct);
        await gate.Ensure(ct);
        await collection.UpdateOneAsync(new BsonDocument{["_id"]=id,["body"]=row["body"]},new BsonDocument("$set",new BsonDocument{
            ["state"]="READY",["bucket"]=bucket,["objectKey"]=objectKey,["sha256"]=hash,["bytes"]=file.Length,["completedAt"]=DateTime.UtcNow}),cancellationToken:ct);
    }
    internal static async Task Schedule(MongoDbContext db, IBackgroundJobClient? jobs, string reportId, CancellationToken ct)
    {
        if(jobs==null)return; // The durable intent remains pending for retry on the next authorized read/apply.
        await AggregateContentRetentionJob.Schedule(db, jobs, reportId, ct);
        var collection=db.Db.GetCollection<BsonDocument>(AggregateCollections.ContentSnapshots);
        var rows=await collection.Find(new BsonDocument{["target"]=reportId,["state"]=new BsonDocument("$ne","READY")}).Limit(100).ToListAsync(ct);
        foreach(var row in rows)
        {
            var id=row["_id"].AsString;
            var claim=await collection.UpdateOneAsync(new BsonDocument{["_id"]=id,["$or"]=new BsonArray{
                new BsonDocument("dispatchUntil",new BsonDocument("$exists",false)),new BsonDocument("dispatchUntil",new BsonDocument("$lt",DateTime.UtcNow))}},
                new BsonDocument("$set",new BsonDocument("dispatchUntil",DateTime.UtcNow.AddMinutes(10))),cancellationToken:ct);
            if(claim.ModifiedCount==0)continue;
            try{jobs.Enqueue<AggregateContentSnapshotJob>(worker=>worker.Run(id,CancellationToken.None));}
            catch{await collection.UpdateOneAsync(new BsonDocument("_id",id),new BsonDocument("$unset",new BsonDocument("dispatchUntil","")),cancellationToken:ct);}
        }
    }
}
