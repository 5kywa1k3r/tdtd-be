using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class TextPinBatchChecks
{
    internal static async Task Run(MongoDbContext db,RuntimeFixture f,string run,Action<bool,string> check,CancellationToken ct)
    {
        var transient=new MongoException("fixture transaction aborted");transient.AddErrorLabel("TransientTransactionError");
        var unknown=new MongoException("fixture commit uncertain");unknown.AddErrorLabel("TransientTransactionError");unknown.AddErrorLabel("UnknownTransactionCommitResult");
        check(AggregateMaterializationWorker.CanRetryRolledBackTransaction(transient),"SL1 classifies real Mongo transient label");
        check(!AggregateMaterializationWorker.CanRetryRolledBackTransaction(unknown),"SL1 unknown commit label takes precedence over transient");
        check(!AggregateMaterializationWorker.CanRetryRolledBackTransaction(new InvalidOperationException("Please retry your operation or multi-document transaction")),"SL1 non-Mongo message cannot enable retry");
        check(!AggregateMaterializationWorker.CanRetryRolledBackTransaction(new MongoException("permanent fixture error")),"SL1 permanent Mongo error cannot enable retry");
        check(AggregateMaterializationWorker.CanRetryRolledBackTransaction(new InvalidOperationException("wrapper",transient)),"SL1 retains known rolled-back Mongo classification through wrapper");
        var reader=new AggregateMongoCommandReader(db,new WorkReportPayloadService(db),true);
        var child=await db.WorkAssignments.Find(a=>a.Id==f.Source.WorkAssignmentId&&a.WorkId==f.WorkId).SingleAsync(ct);
        var collection=db.WorkAssignments.CollectionNamespace.CollectionName;
        var pins=new List<AggregateAuthorityPin>();
        await reader.PinMany(pins,Enumerable.Repeat((collection,child.Id),600),ct);
        check(pins.Count==1&&reader.PinBatchQueryCount==1,"SL2 600 repeated real IDs use one captured pin and query");
        var changed=run+"-pin-mutation";
        var result=await db.WorkAssignments.UpdateOneAsync(a=>a.Id==child.Id&&a.WorkId==f.WorkId&&a.Name==child.Name,
            Builders<WorkAssignment>.Update.Set(a=>a.Name,changed),cancellationToken:ct);
        check(result.ModifiedCount==1,"SL2 changes only own fixture metadata by CAS");
        try{
            var stale=false;
            try{await reader.PinMany(pins,[(collection,child.Id)],ct);}catch(AggregatePreviewException ex) when(ex.Code=="AGG_INPUT_STALE"){stale=true;}
            check(stale,"SL2 repeated capture detects changed metadata rather than trusting prior pin");
        }finally{
            var restored=await db.WorkAssignments.UpdateOneAsync(a=>a.Id==child.Id&&a.WorkId==f.WorkId&&a.Name==changed,
                Builders<WorkAssignment>.Update.Set(a=>a.Name,child.Name),cancellationToken:ct);
            if(restored.ModifiedCount!=1)throw new InvalidOperationException("Own SL2 fixture metadata restore CAS failed.");
        }
        await reader.PinMany(pins,[(collection,child.Id)],ct);
        check(pins.Count==1,"SL2 restored metadata rereads consistently without duplicate pin");
        var uppercase=new List<AggregateAuthorityPin>();
        await reader.PinMany(uppercase,[(collection,child.Id.ToUpperInvariant())],ct);
        check(uppercase.Single().Fingerprint==pins.Single().Fingerprint,"SL2 preserves ObjectId uppercase input compatibility");
        var missing=false;
        try{await reader.PinMany([],[(collection,MongoDB.Bson.ObjectId.GenerateNewId().ToString())],ct);}catch(AggregatePreviewException ex) when(ex.Code=="AGG_CONTEXT_UNAVAILABLE"){missing=true;}
        check(missing,"SL2 missing required metadata fails closed");
    }
}
