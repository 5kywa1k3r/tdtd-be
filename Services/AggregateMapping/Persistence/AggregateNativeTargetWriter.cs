using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal sealed class AggregateNativeTargetWriter(MongoDbContext db, IClientSessionHandle session,
    IWorkReportPayloadReader reader, IWorkReportPayloadWriter writer)
{
    internal async Task<long> WriteAsync(AggregateCommitAuthority authority, AggregateTargetWrite write, CancellationToken ct)
    {
        if (!session.IsInTransaction) throw new InvalidOperationException("AGG_TRANSACTION_REQUIRED");
        var context = authority.Read.Context;
        await AggregateContentScopeGate.Fence(db, session, context.ReportId!, ct);
        var report = await db.WorkAssignmentReports.Find(session, r => r.Id == context.ReportId && !r.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        if (report.Status != WorkAssignmentReportStatus.Draft || !report.IsCurrent || !report.IsActive
            || report.AssigneeUserId != authority.Actor || !string.IsNullOrEmpty(report.PayloadMutationCommandId)
            || report.PayloadRevision != authority.Read.Revisions.PayloadRevision || report.LifecycleRevision != authority.Read.Revisions.LifecycleRevision)
            throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
        var form = await db.DynamicFormTemplates.Find(session, f => f.Id == report.DynamicFormTemplateId && !f.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
        var schema = AggregateNativePayloadAdapter.Schema(form);
        if (schema.Pin != authority.Read.TargetSchema.Pin || !form.IsPublished) throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
        var payload = await reader.LoadReportPayloadAsync(report, ct);
        WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, payload);
        // Build every native definition from the pinned Form only on first
        // Apply. The authoritative validator/writer still validates the whole
        // envelope; unrelated matrix rows remain present with unentered cells.
        var targetTables = AggregateNativePayloadAdapter.IsUninitializedTarget(payload)
            && DynamicFormNativeTableDefinition.IsNative(form)
            ? AggregateViewPayload.EmptyTables(form).ToJsonString() : payload.TableValuesJson;
        var (fieldJson, tableJson) = await AggregateTargetPayload.ValidateAsync(db, session, form, report.Id, write,
            payload.FieldValuesJson, targetTables, async (member, reference) => {
                var tx = new AggregateMongoTransaction(db, session, reader, writer);
                var key = AggregateCanonical.Key(report.Id, member);
                var existing = await tx.GetAsync<AggregateNativeContentBinding>(AggregateCollections.NativeContent, key, ct);
                await tx.PutAsync(AggregateCollections.NativeContent, key, existing?.Version ?? 0,
                    new AggregateNativeContentBinding(report.Id, member, report.DynamicFormSchemaHash!, reference), report.WorkId, report.Id, [], ct);
            }, ct);
        var tables = JsonNode.Parse(tableJson)!.AsObject();
        var now = DateTime.UtcNow;
        var resultWrite = await writer.SaveReportPayloadAsync(report, payload.Values1DJson, fieldJson, tableJson,
            payload.SummarySourceJson, authority.Actor, now, ct, session);
        foreach (var table in tables["nativeTables"]?["tables"]?.AsArray() ?? new JsonArray())
        {
            if(table?["contentRef"] is not {} contentRef)continue;
            var tableId=table["tableId"]!.GetValue<string>();
            var reference=contentRef.Deserialize<AggregateContentReference>(AggregateCanonical.Json)!;
            var tx=new AggregateMongoTransaction(db,session,reader,writer);
            var key=AggregateCanonical.Key(report.Id,resultWrite.PayloadRevision.ToString(CultureInfo.InvariantCulture),tableId);
            await tx.PutAsync(AggregateCollections.ContentSnapshots,key,0,
                new AggregateContentSnapshotIntent(report.Id,tableId,resultWrite.PayloadRevision,reference),report.WorkId,report.Id,[],ct);
        }
        await AggregateContentRetentionJob.PlanOnSave(db, session, report.Id, report.WorkId, ct);
        await AggregatePreviewInvalidation.CancelSuperseded(db, session, report.WorkId, ct, target: report.Id);
        var commit = await db.WorkAssignmentReports.UpdateOneAsync(session,
            r => r.Id == report.Id && r.Status == WorkAssignmentReportStatus.Draft && r.IsActive && r.IsCurrent && !r.IsDeleted
                && r.PayloadRevision == report.PayloadRevision && r.LifecycleRevision == report.LifecycleRevision
                && r.AssigneeUserId == authority.Actor && r.PayloadMutationCommandId == null,
            Builders<WorkAssignmentReport>.Update.Set(r => r.PayloadRevision, resultWrite.PayloadRevision)
                .Set(r => r.PayloadHash, resultWrite.PayloadHash).Set(r => r.PayloadStatus, resultWrite.PayloadStatus)
                .Set(r => r.PayloadSizeBytes, resultWrite.PayloadSizeBytes).Set(r => r.PayloadUpdatedAtUtc, now)
                .Set(r => r.UpdatedAtUtc, now).Set(r => r.UpdatedByUserId, authority.Actor), cancellationToken: ct);
        if (commit.ModifiedCount != 1) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
        await WorkDirectSourceRevisionFence.IncrementAsync(db, session, report.WorkId, ct);
        await AggregateRefreshService.InvalidateAsync(new AggregateMongoTransaction(db, session, reader, writer), report.WorkId,
            ["REPORT:" + report.Id], "APPLY:" + write.Instance.Id + ":" + write.Instance.Revision, ct);
        return resultWrite.PayloadRevision;
    }
}
