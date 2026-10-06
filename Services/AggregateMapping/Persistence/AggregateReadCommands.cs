using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal sealed record AggregateInstanceReadResult(AggregateInstanceState Instance, AggregateRefreshIntent? Refresh,
    string ResultFreshness, IReadOnlyList<AggregateTargetResultDto> LastResults, string? ResultReadError = null);
internal sealed record AggregatePersistedContextResult(AggregatePeriodContextDto Context, AggregateExpectedRevisionsDto Revisions,
    AggregateDataWindowDeclarationDto? DataWindow, AggregateConfigHead? Config, string? InstanceId);

internal sealed partial class AggregateCommandService
{
    internal async Task<object> ReadComputationStatusAsync(AggregateCommandContext command, AggregatePeriodContextDto context, string id, CancellationToken ct)
    {
        var authority = await reader.AuthorizeStatusAsync(context, command.Actor, command.SessionKey, ct);
        return await store.ExecuteAsync<object>(async (tx, token) => {
            var instance = (await Required<AggregateInstanceState>(tx, AggregateCollections.Instances, id, token)).Value;
            if (InstanceKey(instance.Context) != InstanceKey(authority.Read.Context)) throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
            var refresh = await tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh, AggregateRefreshService.IntentKey(id, instance.Generation), token);
            // Target job metadata only: no values, source counts/IDs, trace or input fingerprints.
            return new { instance.Revision, instance.Generation, instance.State, RefreshState = refresh?.Value.State };
        }, ct);
    }
    internal async Task<AggregatePersistedContextResult> ReadContextAsync(AggregateCommandContext command,
        AggregatePeriodContextDto selector, CancellationToken ct)
    {
        var authority = await reader.AuthorizeAsync(selector, command.Actor, command.SessionKey, ct);
        Allow(AggregateAction.ReadConfig, authority);
        return await store.ExecuteAsync(async (tx, token) =>
        {
            var context = authority.Read.Context;
            var head = await tx.GetAsync<AggregateConfigHead>(AggregateCollections.Configs,
                AggregateCanonical.Key(context.WorkId, context.AssignmentId, context.BindingId), token);
            if (head != null) Scope(head.Value, authority);
            var instance = AggregateTargetIdentity.Id(context) == null ? null
                : await tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, InstanceKey(context), token);
            return new AggregatePersistedContextResult(context, authority.Read.Revisions with
            {
                ConfigRevision = instance?.Value.ConfigRevision ?? head?.Value.HeadRevision ?? 0,
                InstanceRevision = instance?.Value.Revision ?? 0
            }, authority.Read.DataWindow, head?.Value, instance?.Value.Id);
        }, ct);
    }
    internal async Task<AggregateInstanceReadResult> ReadInstanceAsync(AggregateCommandContext command, AggregatePeriodContextDto context, string id, CancellationToken ct)
    {
        var authority = await reader.AuthorizeAsync(context, command.Actor, command.SessionKey, ct);
        return await store.ExecuteAsync(async (tx, token) =>
        {
            var instance = (await Required<AggregateInstanceState>(tx, AggregateCollections.Instances, id, token)).Value;
            if (InstanceKey(instance.Context) != InstanceKey(authority.Read.Context)) throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
            var refresh = await tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh, AggregateRefreshService.IntentKey(id, instance.Generation), token);
            var freshness = instance.State == "ARCHIVED" ? "ARCHIVED" : instance.State == "FROZEN" ? "FROZEN_SNAPSHOT" : instance.State == "NEEDS_REPAIR" ? "NEEDS_REPAIR"
                : refresh?.Value.State is "PENDING" or "RUNNING" or "FAILED" or "CANCELLED" ? refresh.Value.State
                : instance.Applied == null ? "NOT_APPLIED" : "AS_OF_LAST_APPLY";
            // A saved report is readable by its current owner; reading a saved mapping must
            // not reveal child payload/lineage after source permission has been revoked.
            // Fresh preview rechecks whole-source ACL before returning that evidence again.
            // Verify source ACL/current headers before exposing any partial values.
            // Collections and text remain lazy readbacks after complete publication.
            var status = refresh == null ? null : refresh.Value with { Lease = null,
                Progress = refresh.Value.Progress == null ? null : refresh.Value.Progress with { Results = [] } };
            if (status?.Progress != null && status.InputStamp != null) {
                var version = (await Required<AggregateConfigVersion>(tx, AggregateCollections.Versions, VersionKey(instance.ConfigId, instance.ConfigRevision), token)).Value;
                try {
                    if (status.InputStamp == await reader.InputStampAsync(instance, AggregateOverlay.Effective(version, instance), authority, token))
                        status = status with { Progress = status.Progress with { Results = refresh!.Value.Progress!.Results.Select(r =>
                            r.ValueType == "NUMBER" ? WithoutSourceLineage(r) : r with { State = "WAITING", Value = null, LineageRef = "" }).ToArray() } };
                } catch (AggregatePreviewException) { status = status with { Progress = null }; }
            }
            var readError = authority.Read.SavedResultReadError;
            if (readError == null && instance.Applied != null)
            {
                try { await reader.ValidateSavedResultAsync(authority, instance.Applied, token); }
                catch (AggregatePreviewException ex) { readError = ex.Code; }
            }
            return new AggregateInstanceReadResult(instance with { Applied = null }, status, freshness,
                readError == null ? instance.Applied?.Preview.Results.Select(WithoutSourceLineage).ToArray() ?? [] : [], readError);
        }, ct);
    }
    internal static AggregateTargetResultDto WithoutSourceLineage(AggregateTargetResultDto result)
    {
        var value = result.Value;
        if (result.ValueType == "TABLE" && value.HasValue)
        {
            var table = JsonNode.Parse(value.Value.GetRawText());
            if (table?["rows"] is JsonArray rows)
                foreach (var row in rows)
                {
                    if(row is JsonObject rowObject)rowObject.Remove("note");
                    if (row?["cells"] is JsonArray cells)
                        foreach (var cell in cells.OfType<JsonObject>()) cell.Remove("lineage");
                }
            value = JsonSerializer.SerializeToElement(table);
        }
        return result with { Value = value, LineageRef = "" };
    }
    internal async Task<object> ReadConfigAsync(AggregateCommandContext command, AggregatePeriodContextDto context, string id, CancellationToken ct, long? revision = null)
    {
        var authority = await reader.AuthorizeAsync(context, command.Actor, command.SessionKey, ct);
        return await store.ExecuteAsync(async (tx, token) =>
        {
            var head = (await Required<AggregateConfigHead>(tx, AggregateCollections.Configs, id, token)).Value; Scope(head, authority);
            if (revision is < 1 || revision > head.HeadRevision) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
            var version = (await Required<AggregateConfigVersion>(tx, AggregateCollections.Versions, VersionKey(id, revision ?? head.HeadRevision), token)).Value;
            var instances = await tx.QueryAsync<AggregateInstanceState>(AggregateCollections.Instances, new(head.WorkId, DependencyKey: "CONFIG:" + id), token);
            // Display the pinned period from this binding's existing instances; no child payload or new report lookup.
            return (object)new { Head = head, Version = version, Instances = instances.Select(i => new { i.Value.Id, i.Value.Context.ReportId, i.Value.Context.PeriodKey, i.Value.ConfigRevision, i.Value.Revision, i.Value.State }) };
        }, ct);
    }
}
