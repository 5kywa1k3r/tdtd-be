using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.Services.AggregateMapping.Persistence;

// Called only AFTER the existing owner has validated its command, inside that owner's
// transaction. It cannot grant report/reviewer permissions or start a lifecycle command.
internal static partial class AggregateHostIntegration
{
    internal static async Task<AggregateReportEditHintDto> ReadEditHintAsync(MongoDbContext db,
        WorkAssignmentReport report, string actor, CancellationToken ct)
    {
        // The caller has already authorized reading this report. Read only its own target claims;
        // no source payload, source permission, or lineage is disclosed here. A hint can become
        // stale after this read; GuardPayloadAsync still checks claims in the write transaction.
        var rows = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Targets)
            .Find(new BsonDocument { { "workId", report.WorkId }, { "target", report.Id } })
            .Project(new BsonDocument("body", 1)).Limit(1001).ToListAsync(ct);
        if (rows.Count > 1000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        var claims = rows.Select(row => System.Text.Json.JsonSerializer.Deserialize<AggregateTargetClaim>(
            row["body"].AsString, AggregateCanonical.Json) ?? throw new AggregatePreviewException("AGG_TARGET_CLAIM_INVALID")).ToArray();
        return AggregateReportEditHints.Build(report.Id, actor, report.PayloadRevision, report.LifecycleRevision,
            report.DynamicFormSchemaHash ?? "", claims);
    }

    // A read-only hint on the already-authorized report. The lifecycle transaction
    // still rechecks mappings, revisions, source ACL and confirmation independently.
    internal static async Task<bool> RequiresSubmissionPreviewAsync(MongoDbContext db, WorkAssignmentReport report, CancellationToken ct)
    {
        var rows = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances)
            .Find(new BsonDocument { { "workId", report.WorkId }, { "target", report.Id } })
            .Project(new BsonDocument("body", 1)).Limit(1001).ToListAsync(ct);
        if (rows.Count > 1000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        return rows.Any(row => (System.Text.Json.JsonSerializer.Deserialize<AggregateInstanceState>(
            row["body"].AsString, AggregateCanonical.Json) ?? throw new AggregatePreviewException("AGG_INSTANCE_INVALID")).State != "UNLINKED");
    }

    internal static AggregateMongoTransaction Transaction(MongoDbContext db, IClientSessionHandle session)
    {
        if (!session.IsInTransaction) throw new InvalidOperationException("AGG_TRANSACTION_REQUIRED");
        var payload = new WorkReportPayloadService(db);
        return new(db, session, payload, payload);
    }

    internal static async Task<bool> HasWorkAsync(MongoDbContext db, IClientSessionHandle session, string workId, CancellationToken ct)
    {
        foreach (var name in new[] { AggregateCollections.Configs, AggregateCollections.Declarations, AggregateCollections.Locks })
            if (await db.Db.GetCollection<BsonDocument>(name).Find(session, new BsonDocument("workId", workId)).AnyAsync(ct)) return true;
        return false;
    }

    internal static async Task<AggregatePeriodContextDto> ContextAsync(MongoDbContext db, IClientSessionHandle session,
        WorkAssignmentReport report, CancellationToken ct)
    {
        var period = string.IsNullOrEmpty(report.WorkReportPeriodId) ? null : await db.WorkReportPeriods
            .Find(session, p => p.Id == report.WorkReportPeriodId && !p.IsDeleted).FirstOrDefaultAsync(ct);
        var bindings = await db.WorkTemplateAssignees.Find(session, b => b.WorkId == report.WorkId
            && b.WorkAssignmentId == report.WorkAssignmentId && !b.IsDeleted
            && (period != null ? b.Id == period.WorkTemplateAssigneeId
                : b.AssigneeUserId == report.AssigneeUserId && b.DynamicFormTemplateId == report.DynamicFormTemplateId)).Limit(2).ToListAsync(ct);
        if (bindings.Count != 1) throw new AggregatePreviewException("AGG_SOURCE_BINDING_AMBIGUOUS");
        var binding = bindings[0];
        return new(binding.AssignmentType == "ONCE" ? "ONCE" : "PERIODIC", report.WorkId, report.WorkAssignmentId,
            binding.Id, report.Id, period?.Id, report.PeriodInstanceKey, period?.PeriodKey ?? report.PeriodKey,
            null, null, null, "");
    }

    internal static async Task GuardReportAsync(MongoDbContext db, IClientSessionHandle session,
        WorkAssignmentReport report, CancellationToken ct)
    {
        if (!await HasWorkAsync(db, session, report.WorkId, ct)) return;
        var assignment = await db.WorkAssignments.Find(session, a => a.Id == report.WorkAssignmentId).FirstOrDefaultAsync(ct);
        if (assignment != null && DynamicFlowBranchVisibility.IsFlowAssignment(assignment)) return;
        var context = await ContextAsync(db, session, report, ct);
        await AggregateLifecycleParticipant.EnsureMutationAsync(Transaction(db, session), report.Id, context.BindingId,
            Occurrence(context), ct);
    }

    internal static string Occurrence(AggregatePeriodContextDto context)
        => context.Kind == "ONCE" ? "ONCE" : context.PeriodKey ?? throw new AggregatePreviewException("AGG_OCCURRENCE_UNRESOLVED");

    internal static async Task GuardPayloadAsync(MongoDbContext db, IClientSessionHandle session,
        WorkAssignmentReport before, string? fields, string? tables, CancellationToken ct)
    {
        await GuardReportAsync(db, session, before, ct);
        var tx = Transaction(db, session);
        var claims = await tx.QueryAsync<AggregateTargetClaim>(AggregateCollections.Targets, new(before.WorkId, before.Id), ct);
        if (claims.Count == 0) return;
        var payload = await new WorkReportPayloadService(db).LoadReportPayloadAsync(before, ct);
        WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(before, payload);
        AggregateMappedMembers.EnsureUnchanged(claims.Select(c => c.Value.MemberId), payload.FieldValuesJson,
            Values1DCompression.ExpandTableValuesJson(payload.TableValuesJson, AggregateCanonical.Json), fields,
            Values1DCompression.ExpandTableValuesJson(tables, AggregateCanonical.Json));
    }

    internal static async Task InvalidateReportAsync(MongoDbContext db, IClientSessionHandle session,
        WorkAssignmentReport report, string eventId, CancellationToken ct)
    {
        if (!await HasWorkAsync(db, session, report.WorkId, ct)) return;
        var assignment = await db.WorkAssignments.Find(session, a => a.Id == report.WorkAssignmentId).FirstOrDefaultAsync(ct);
        if (assignment != null && DynamicFlowBranchVisibility.IsFlowAssignment(assignment)) return;
        var context = await ContextAsync(db, session, report, ct);
        await AggregateRefreshService.InvalidateAsync(Transaction(db, session), report.WorkId,
            ["REPORT:" + report.Id, "SLOT:" + context.BindingId + ":" + Occurrence(context), "MEMBERSHIP:" + assignment?.ParentAssignmentId], eventId, ct);
    }

    internal static async Task LifecycleAsync(MongoDbContext db, IClientSessionHandle session,
        IDynamicFlowDefinitionTransactionRunner transactions, WorkAssignmentReport before, string operation,
        string actor, string commandId, WorkAssignmentReportStatus resultStatus, int resultRevision,
        CancellationToken ct, IConfiguration? configuration = null, string? sessionKey = null, string? confirmation = null)
    {
        await GuardReportAsync(db, session, before, ct);
        var tx = Transaction(db, session);
        var instances = await tx.QueryAsync<AggregateInstanceState>(AggregateCollections.Instances, new(before.WorkId, before.Id), ct);
        if (!instances.Any(i => i.Value.State != "UNLINKED")) return;
        if (before.Status == resultStatus) return; // Active/version changes still pass the source guard.
        var context = await ContextAsync(db, session, before, ct);
        var payload = new WorkReportPayloadService(db);
        var reader = new AggregateMongoCommandReader(db, payload, AggregateIntegrationGate.V2Ready(configuration));
        AggregateCommitAuthority authority;
        AggregateConfirmationTokens? tokens = null;
        if (before.Status == WorkAssignmentReportStatus.Draft)
        {
            tokens = Tokens(configuration);
            authority = await reader.AuthorizeAsync(context, actor, sessionKey ?? "", ct);
        }
        else
        {
            // Review authority is the existing validated owner command. Never impersonate the
            // report assignee to read child values. Return/recall use the frozen snapshot only.
            var pin = new AggregateFormPinDto(before.DynamicFormTemplateId!, "", 0, before.DynamicFormSchemaHash ?? "");
            var facts = new AggregateAuthorityFacts(true, true, false, true, false, false, false, false,
                true, false, true, before.IsActive, before.Status.ToString(), false, false, false, true, true);
            authority = new(actor, "existing-lifecycle:" + commandId, new(context, new(pin, new Dictionary<string, AggregateMember>()),
                facts, new(before.PayloadRevision, before.LifecycleRevision, 0, 0, pin.SchemaHash, ""), "", null,
                new Dictionary<string, AggregateValue>()), []);
        }
        var submissionIds = instances.Where(i => i.Value.State == "FROZEN").Select(i => i.Value.SubmissionId).Distinct().ToArray();
        if (submissionIds.Length > 1) throw new AggregatePreviewException("AGG_SUBMISSION_STALE");
        var submissionId = before.Status == WorkAssignmentReportStatus.Draft ? commandId
            : submissionIds.SingleOrDefault() ?? commandId;
        var participant = new AggregateLifecycleParticipant(new AggregateMongoStore(db, transactions, payload, payload), reader, tokens);
        await participant.BeforeCommitAsync(tx, new(commandId, operation, before.Id, actor, authority.SessionKey, DateTimeOffset.UtcNow),
            authority, new(before.Id, operation, before.Status.ToString(), resultStatus.ToString(), before.PayloadRevision,
                before.LifecycleRevision, resultRevision, submissionId, true, true, before.AssigneeUserId), confirmation, ct);
    }

    internal static AggregateConfirmationTokens Tokens(IConfiguration? configuration)
    {
        try
        {
            var key = Convert.FromBase64String(configuration?["AggregateMapping:ConfirmationKeyBase64"] ?? "");
            if (key.Length >= 32) return new(key);
        }
        catch (FormatException) { }
        throw new AggregatePreviewException("AGG_CONFIRMATION_KEY_UNAVAILABLE");
    }
}
