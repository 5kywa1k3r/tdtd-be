using MongoDB.Driver;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Data;
using tdtd_be.Services;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

internal static class ListL4BrowserFixture
{
    internal static async Task<DynamicFormTemplate> Publish(MongoDbContext db,DynamicFormTemplate form,string catalogId,string catalogOwner,CancellationToken ct)
    {
        if(form.IsPublished||!form.Name.StartsWith("list-l4-",StringComparison.Ordinal))throw new InvalidOperationException("Only a new L4 draft fixture may be published.");
        var actor=await db.Users.Find(u=>u.Id==form.CreatedByUserId).SingleAsync(ct);
        // The catalog and Form belong to the same newly seeded designer; no existing catalog is touched.
        await db.LabelEnumCatalogs.UpdateOneAsync(c=>c.Id==catalogId&&c.CreatedByUsername==catalogOwner,
            Builders<LabelEnumCatalog>.Update.Set(c=>c.CreatedByUsername,actor.Username),cancellationToken:ct);
        var http=new DefaultHttpContext();http.Items[MeAccessor.MeItemKey]=new MeResponse(actor.Id,actor.Username,"L4 fixture",[],actor.UnitId!,null,null,null,[],null,false);
        var me=new MeAccessor(new HttpContextAccessor{HttpContext=http});
        var config=new DynamicFormStatisticConfigCommandService(db,me,new StatConfigTransactionRunner(db,NullLogger<StatConfigTransactionRunner>.Instance));
        var service=new DynamicFormService(db,me,config,new LabelEnumCatalogService(db,me));
        await service.PublishAsync(form.Id,new(form.Revision),ct);
        var published=await db.DynamicFormTemplates.Find(t=>t.Id==form.Id).SingleAsync(ct);
        _=DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(published,published.StatisticConfigId!,published.StatisticConfigVersionId!,published.StatisticConfigVersionNo,published.StatisticConfigRevision,published.StatisticConfigHash!);
        Console.WriteLine($"PASS L4 fixture normal publish locks empty native statistic config before assignment/report pins: form={form.Id}");
        return published;
    }
    internal static async Task Prepare(MongoDbContext db,string workId,CancellationToken ct)
    {
        var work=await db.Works.Find(w=>w.Id==workId&&!w.IsDeleted).SingleAsync(ct);
        var actor=await db.Users.Find(u=>u.Id==work.CreatedByUserId&&!u.IsDeleted).SingleAsync(ct);
        if(!work.AutoCode.StartsWith("list-l4-",StringComparison.Ordinal)||!actor.Username.StartsWith("list-l4-",StringComparison.Ordinal))
            throw new InvalidOperationException("Only this harness's isolated L4 fixture may be prepared.");
        var projection=new DocRoleReadModelProjectionService(db);
        var roles=new DocRoleService(db,projection);
        await roles.UpsertWorkRootRolesAsync(work,ct);
        foreach(var assignment in await db.WorkAssignments.Find(a=>a.WorkId==workId&&!a.IsDeleted).ToListAsync(ct))
            await roles.UpsertWorkAssignmentRolesAsync(assignment,ct);
        await roles.RebuildWorkParticipantRolesFromAssignmentsAsync(workId,actor.Id,ct);
        await projection.RebuildWorkAssignmentsAsync(workId,actor.Id,ct);
        await projection.RebuildWorkReportPeriodsAsync(workId,actor.Id,ct);
        Console.WriteLine($"PASS L4 fixture product role projections rebuilt from authoritative assignments: work={workId}");
    }
}
