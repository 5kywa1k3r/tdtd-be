using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models;
using tdtd_be.Services;
using tdtd_be.Services.Common;

internal static class ExtendedBrowserFixture
{
    internal static async Task Prepare(string workId)
    {
        var db=new MongoDbContext(Options.Create(new MongoOptions {ConnectionString="mongodb://localhost:27017/?replicaSet=tdtd-rs",Database="tdtd"}));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));var ct=timeout.Token;
        var work=await db.Works.Find(w=>w.Id==workId&&!w.IsDeleted).SingleAsync(ct);
        var actor=await db.Users.Find(u=>u.Id==work.CreatedByUserId&&!u.IsDeleted).SingleAsync(ct);
        if(!work.AutoCode.StartsWith("operators-v3-",StringComparison.Ordinal)||!actor.Username.StartsWith("operators-v3-",StringComparison.Ordinal))throw new InvalidOperationException("Only own operators-v3 fixture may be prepared.");
        if(string.IsNullOrEmpty(actor.PasswordHash)){
            var password=Environment.GetEnvironmentVariable("AGG_FIXTURE_PASSWORD")??throw new InvalidOperationException("Fixture password must be supplied in process environment.");
            actor.Username=actor.Username.ToLowerInvariant();actor.FullName="Kiểm thử phép tổng hợp v3 — Dữ liệu giả";
            actor.PasswordHash=new PasswordHasher<AppUser>().HashPassword(actor,password);
            await db.Users.ReplaceOneAsync(u=>u.Id==actor.Id&&u.Username.StartsWith("operators-v3-")&&u.PasswordHash==null,actor,cancellationToken:ct);
        }
        var projection=new DocRoleReadModelProjectionService(db);var roles=new DocRoleService(db,projection);
        await roles.UpsertWorkRootRolesAsync(work,ct);
        foreach(var assignment in await db.WorkAssignments.Find(a=>a.WorkId==workId&&!a.IsDeleted).ToListAsync(ct))await roles.UpsertWorkAssignmentRolesAsync(assignment,ct);
        await roles.RebuildWorkParticipantRolesFromAssignmentsAsync(workId,actor.Id,ct);
        await projection.RebuildWorkAssignmentsAsync(workId,actor.Id,ct);await projection.RebuildWorkReportPeriodsAsync(workId,actor.Id,ct);
        Console.WriteLine($"V3_BROWSER work={workId} username={actor.Username} actor={actor.Id}; fixture only, normal product authorization.");
    }
}
