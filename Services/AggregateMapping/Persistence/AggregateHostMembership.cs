using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal static partial class AggregateHostIntegration
{
    internal static async Task EffectivenessAsync(MongoDbContext db, IClientSessionHandle session,
        string workId, string assignmentId, string eventId, CancellationToken ct)
    {
        // All descendants inherit effectiveness, but retain their own IsActive/completion flags.
        // Check every frozen consumer before changing membership and invalidate every affected target.
        var nodes = await db.WorkAssignments.Find(session, a => a.WorkId == workId && !a.IsDeleted)
            .Project(a => new { a.Id, a.ParentAssignmentId }).ToListAsync(ct);
        var children = nodes.Where(a => a.ParentAssignmentId != null).ToLookup(a => a.ParentAssignmentId!);
        var affected = new HashSet<string> { assignmentId };
        var pending = new Queue<string>(); pending.Enqueue(assignmentId);
        while (pending.TryDequeue(out var id))
            foreach (var child in children[id]) if (affected.Add(child.Id)) pending.Enqueue(child.Id);
        await RelationshipAsync(db, session, workId, affected.ToArray(), eventId, ct);
    }
    internal static Task<bool> IsPeriodicWorkAsync(MongoDbContext db, string workId, CancellationToken ct)
        => db.Db.GetCollection<BsonDocument>(AggregateCollections.Configs).Find(new BsonDocument("workId", workId)).AnyAsync(ct);
    internal static async Task<bool> PeriodicOwnedAsync(MongoDbContext db, string reportId, CancellationToken ct)
    {
        var rows = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("target", reportId)).ToListAsync(ct);
        return rows.Any(r => AggregateMongoTransaction.Read<AggregateInstanceState>(r).Value.State != "UNLINKED");
    }
    internal static async Task EnsureLegacyRefreshAsync(MongoDbContext db, IClientSessionHandle session, WorkAssignmentReport report, CancellationToken ct)
    {
        var instances = await Transaction(db, session).QueryAsync<AggregateInstanceState>(AggregateCollections.Instances, new(report.WorkId, report.Id), ct);
        if (instances.Any(i => i.Value.State != "UNLINKED")) throw new AggregatePreviewException("AGG_PERIODIC_REFRESH_OWNS_TARGET");
    }
    internal static async Task SlotAsync(MongoDbContext db, IClientSessionHandle session, string workId,
        string bindingId, string occurrence, string eventId, CancellationToken ct)
    {
        if (!await HasWorkAsync(db, session, workId, ct)) return;
        if (string.IsNullOrWhiteSpace(bindingId) || string.IsNullOrWhiteSpace(occurrence))
            throw new AggregatePreviewException("AGG_OCCURRENCE_UNRESOLVED");
        var tx = Transaction(db, session);
        await AggregateLifecycleParticipant.EnsureMutationAsync(tx, "", bindingId, occurrence, ct);
        var binding = await db.WorkTemplateAssignees.Find(session, b => b.Id == bindingId && b.WorkId == workId).FirstOrDefaultAsync(ct);
        var assignment = binding == null ? null : await db.WorkAssignments.Find(session, a => a.Id == binding.WorkAssignmentId).FirstOrDefaultAsync(ct);
        await AggregateRefreshService.InvalidateAsync(tx, workId,
            ["SLOT:" + bindingId + ":" + occurrence, "MEMBERSHIP:" + assignment?.ParentAssignmentId], eventId, ct);
    }

    internal static async Task RelationshipAsync(MongoDbContext db, IClientSessionHandle session,
        string workId, IReadOnlyList<string> assignmentIds, string eventId, CancellationToken ct, bool preserveIdentity = false)
    {
        if (!await HasWorkAsync(db, session, workId, ct)) return;
        var bindings = await db.WorkTemplateAssignees.Find(session, b => b.WorkId == workId
            && assignmentIds.Contains(b.WorkAssignmentId) && !b.IsDeleted).ToListAsync(ct);
        var tx = Transaction(db, session);
        if (!preserveIdentity)
            await AggregateMutationParticipant.BeforeRelationshipWriteAsync(tx, workId, assignmentIds,
                bindings.Select(b => b.Id).ToArray(), true, ct);
        // Query-based reverse dependencies also include bindings with missing slots. The Work
        // fence in the caller conflicts with a concurrent submit that acquires a new owner row.
        var keys = assignmentIds.Select(id => "ASSIGNMENT:" + id)
            .Concat(bindings.Select(b => "BINDING:" + b.Id)).ToArray();
        var assignments = await db.WorkAssignments.Find(session, a => a.WorkId == workId && assignmentIds.Contains(a.Id)).ToListAsync(ct);
        await AggregateRefreshService.InvalidateAsync(tx, workId, keys.Concat(assignments.SelectMany(a =>
            new[] { "MEMBERSHIP:" + a.Id, "MEMBERSHIP:" + a.ParentAssignmentId })).ToArray(), eventId, ct);
    }

    internal static async Task HandoverAsync(MongoDbContext db, IClientSessionHandle session, string workId,
        string assignmentId, string bindingId, string toUser, string eventId, CancellationToken ct)
    {
        var tx = Transaction(db, session);
        var view = await tx.GetAsync<AggregateViewState>(AggregateCollections.Views, AggregateCanonical.Key("VIEW", "ASSIGNMENT", assignmentId), ct);
        foreach (var head in await tx.QueryAsync<AggregateConfigHead>(AggregateCollections.Configs, new(workId, assignmentId), ct))
        {
            if (head.Value.BindingId != bindingId && head.Value.BindingId != view?.Value.Current.BindingId) continue;
            await tx.PutAsync(AggregateCollections.Configs, head.Value.Id, head.Version, head.Value with { OwnerUserId = toUser },
                workId, assignmentId, [], ct);
            foreach (var item in await tx.QueryAsync<AggregateInstanceState>(AggregateCollections.Instances, new(workId, DependencyKey: "CONFIG:" + head.Value.Id), ct))
            {
                if (item.Value.State is "FROZEN" or "UNLINKED" or "ARCHIVED") continue;
                var next = item.Value with { AuthorityUserId = toUser, Revision = checked(item.Value.Revision + 1), Generation = checked(item.Value.Generation + 1) };
                await AggregateCommandService.PutInstance(tx, next, item.Version, ct);
                if (next.Applied != null) await AggregateCommandService.Dependencies(tx, next, next.Applied, ct);
                if (next.State == "DRAFT") await AggregateRefreshService.EnqueueAsync(tx, next, eventId, ct);
            }
        }
    }

    internal static async Task CarryDeclarationAsync(MongoDbContext db, IClientSessionHandle session,
        WorkAssignmentReport report, CancellationToken ct)
    {
        if (!await HasWorkAsync(db, session, report.WorkId, ct)) return;
        var context = await ContextAsync(db, session, report, ct);
        var tx = Transaction(db, session);
        var slot = await tx.GetAsync<AggregateDeclarationState>(AggregateCollections.Declarations, "SLOT:" + context.BindingId + ":" + Occurrence(context), ct);
        if (slot == null) return;
        await tx.PutAsync(AggregateCollections.Declarations, "REPORT:" + report.Id, 0,
            new AggregateDeclarationState("REPORT", report.Id, report.WorkId, report.WorkAssignmentId, slot.Value.Declaration with { Revision = 1 }),
            report.WorkId, report.Id, [], ct);
    }

    internal static async Task<int> DispatchAsync(MongoDbContext db, IDynamicFlowDefinitionTransactionRunner transactions, IConfiguration configuration,
        int limit, CancellationToken ct, string? workId = null)
    {
        // This is a handler in the existing lifecycle dispatcher, never a new scheduler.
        if (!AggregateIntegrationGate.RuntimeReady(configuration)) return 0;
        Hangfire.IBackgroundJobClient? jobs;
        try { jobs = new Hangfire.BackgroundJobClient(); }
        catch (InvalidOperationException) { return 0; }
        return await AggregateMaterializationDispatch.Schedule(db, transactions, jobs, limit, ct, workId);
    }

    internal static async Task SeedInstanceAsync(MongoDbContext db, IClientSessionHandle session, WorkAssignmentReport report, CancellationToken ct)
    {
        if (!await HasWorkAsync(db, session, report.WorkId, ct)) return;
        var context = await ContextAsync(db, session, report, ct);
        var tx = Transaction(db, session);
        var configId = AggregateCanonical.Key(context.WorkId, context.AssignmentId, context.BindingId);
        var head = await tx.GetAsync<AggregateConfigHead>(AggregateCollections.Configs, configId, ct);
        if (head == null) return;
        var id = AggregateCommandService.InstanceKey(context);
        if (await tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, id, ct) != null) return;
        var version = (await AggregateCommandService.Required<AggregateConfigVersion>(tx, AggregateCollections.Versions,
            AggregateCommandService.VersionKey(configId, head.Value.HeadRevision), ct)).Value;
        var declaration = await tx.GetAsync<AggregateDeclarationState>(AggregateCollections.Declarations, "REPORT:" + report.Id, ct);
        context = context with { DataStartDate = declaration?.Value.Declaration.StartDate, DataEndDate = declaration?.Value.Declaration.EndDate };
        var compatible = head.Value.TargetForm.SchemaHash == report.DynamicFormSchemaHash && head.Value.TargetForm.FormId == report.DynamicFormTemplateId;
        var selection = new AggregateInstanceSelectionDto(version.Recipe.Nodes.Where(n => n.Kind == "SOURCE")
            .Select(n => new AggregateSourceSelectionDto(n.Id, "FORM_SELECTOR", [], [])).ToList());
        var raw = compatible ? null : new AggregateRawDraftDto(System.Text.Json.JsonSerializer.Serialize(version.Recipe, AggregateCanonical.Json),
            [new("AGG_SCHEMA_INCOMPATIBLE", "$.target", "Pinned configuration needs an explicit migration.")], null);
        var instance = new AggregateInstanceState(id, context, configId, head.Value.HeadRevision, 1, 1,
            compatible ? "DRAFT" : "NEEDS_REPAIR", null, selection, [], raw, null, null, report.AssigneeUserId);
        await AggregateCommandService.PutInstance(tx, instance, 0, ct);
        await AggregateCommandService.ClaimTargets(tx, instance, version.Recipe.Nodes.Single(n => n.Kind == "TARGET").Inputs.Select(p => p.MemberId!).ToArray(), ct);
        var keys = new[] { "MEMBERSHIP:" + context.AssignmentId, "CONFIG:" + configId };
        await tx.PutAsync(AggregateCollections.Dependencies, id, 0, new AggregateDependencyState(id, report.Id, 1, keys, [], []),
            context.WorkId, report.Id, keys, ct);
        if (compatible) await AggregateRefreshService.EnqueueAsync(tx, instance, "OPEN:" + report.Id, ct);
    }
}
