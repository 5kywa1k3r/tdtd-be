using MongoDB.Bson;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

internal sealed record AdvancedNativeComparisonCapture(string SnapshotId, string SnapshotHash,
    string SourceSetHash, DateTime CheckedAtUtc, NativeComparisonResult NativeComparison,
    NativeComparisonIdentity ExpectedIdentity, NativeComparisonIdentity ActualIdentity,
    string ExpectedResultHash, string ActualResultHash, IReadOnlyList<string> AssignmentIds,
    AdvancedNativeComparisonEvidence ExpectedEvidence, AdvancedNativeComparisonEvidence ActualEvidence);

public sealed partial class WorkAssignmentAdvancedSummaryConfigService
{
    public async Task<AdvancedNativeComparisonResponse> CompareNativeSummaryAsync(AdvancedNativeComparisonRequest request, CancellationToken ct)
        => (await ExecuteNativeComparisonAsync(request, false, ct)).Observation;

    public async Task<AdvancedNativeComparisonRecordResponse> RecordNativeComparisonAsync(AdvancedNativeComparisonRequest request, CancellationToken ct)
        => (await ExecuteNativeComparisonAsync(request, true, ct)).Record!;

    private async Task<(AdvancedNativeComparisonResponse Observation, AdvancedNativeComparisonRecordResponse? Record)> ExecuteNativeComparisonAsync(
        AdvancedNativeComparisonRequest request, bool retain, CancellationToken ct)
    {
        LegacyAggregateRetirement.Reject();
        ArgumentNullException.ThrowIfNull(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AdvancedNativeComparisonBudget.Timeout);
        using var lease = AdvancedNativeComparisonBudget.Enter();
        try
        {
            var actor = await LoadNativeAdvancedActorAsync(_me.RequireMe().Id, null, timeout.Token);
            _ = await P805LoadAuthorizedAssignmentAsync(null, P805NormalizeAssignmentId(request.ScopeAssignmentId), actor.Me, true, timeout.Token);
            var capture = await CompareNativeCurrentAsync(new(request.ScopeAssignmentId, request.DynamicFormTemplateId,
                request.SectionId, request.Grain, request.GrainKey, request.SnapshotId), request.SnapshotHash, timeout.Token);
            // Management permission may have changed while recomputing.
            var current = await LoadNativeAdvancedActorAsync(actor.Me.Id, actor.Hash, timeout.Token);
            _ = await P805LoadAuthorizedAssignmentAsync(null, request.ScopeAssignmentId, current.Me, true, timeout.Token);
            var result = capture.NativeComparison;
            var observation = new AdvancedNativeComparisonResponse(1, !result.Comparable ? "INCOMPARABLE" : result.Equivalent ? "MATCH" : "DIFFERENT", "ADVANCED_NATIVE_CURRENT",
                request.ScopeAssignmentId, request.DynamicFormTemplateId, request.SectionId, request.Grain, request.GrainKey,
                capture.SnapshotId, capture.SnapshotHash, capture.SourceSetHash, capture.CheckedAtUtc,
                result.Differences.Select(d => new AdvancedNativeComparisonDifference(d.Reason, d.Address, d.OperationId)).ToArray());
            if (!retain) return (observation, null);
            var record = new AdvancedNativeComparisonRecord(1, MongoDB.Bson.ObjectId.GenerateNewId().ToString(), actor.Me.Id, actor.Hash,
                DateTime.UtcNow, observation, capture.ExpectedIdentity, capture.ActualIdentity,
                capture.ExpectedResultHash, capture.ActualResultHash, capture.AssignmentIds, capture.ExpectedEvidence, capture.ActualEvidence);
            var recordStore = new AdvancedNativeComparisonRecordStore(_ctx.Db);
            var recordHash = await _statConfigTransactions.ExecuteAsync(async (session, token) => {
                RequireNativeAdvancedResultGate();
                var savingActor = await LoadNativeAdvancedActorAsync(actor.Me.Id, actor.Hash, token);
                var scope = await P805LoadAuthorizedAssignmentAsync(session, request.ScopeAssignmentId, savingActor.Me, true, token);
                await StatRunDirectProjectionService.FenceDocumentAsync(session, _ctx.Users, savingActor.Owner,
                    ["_id", "username", "fullName", "unitId", "roles", "positionCode", "accountKind", "isDeleted"], token);
                foreach (var id in capture.AssignmentIds.Append(scope.Id).Distinct(StringComparer.Ordinal)) {
                    var assignment = await P805LoadAuthorizedAssignmentAsync(session, id, savingActor.Me, id == scope.Id, token);
                    await StatRunDirectProjectionService.FenceDocumentAsync(session, _ctx.WorkAssignments, assignment,
                        assignment.ToBsonDocument().Names.Where(n => n != "nativeStatisticPublicationFence").ToArray(), token);
                }
                var snapshot = await new AdvancedNativeSnapshotStore(_ctx.Db).ReadAsync(request.SnapshotId, token, session);
                if (snapshot is null || snapshot.Value.Hash != request.SnapshotHash) throw NativeAdvancedError("ADVANCED_NATIVE_COMPARISON_ARTIFACT_CHANGED");
                // Persist a prior checked observation, not a claim that the source
                // is still current at every later read. No source values repaired.
                return await recordStore.AppendAsync(session, record, token);
            }, timeout.Token);
            return (observation, new(1, record.Id, recordHash, record.ActorId, record.RecordedAtUtc, "HISTORICAL_OBSERVATION", observation));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new AdvancedNativeComparisonBudgetException("ADVANCED_NATIVE_COMPARISON_TIMEOUT"); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("NATIVE_COMPARISON_", StringComparison.Ordinal))
        { throw NativeAdvancedError(ex.Message); }
        catch (InvalidOperationException ex) when (ex.Message == "P9_DIRECT_NATIVE_PUBLICATION_SOURCE_FENCE_STALE")
        { throw NativeAdvancedError("ADVANCED_NATIVE_INPUT_CHANGED_RETRY"); }
    }

    // Capture adapter beneath the bounded observation endpoint; no persisted verdict or repair.
    // Exact CURRENT capture only: today's sources cannot reconstruct old payloads.
    internal async Task<AdvancedNativeComparisonCapture> CompareNativeCurrentAsync(
        AdvancedNativeSummaryRequest request, string snapshotHash, CancellationToken ct)
    {
        if (request.Historical || !StatRunCanonicalJson.IsCanonicalSha256(request.SnapshotId)
            || !StatRunCanonicalJson.IsCanonicalSha256(snapshotHash))
            throw NativeAdvancedError("ADVANCED_NATIVE_COMPARISON_EXACT_CURRENT_REQUIRED");
        var actor = await LoadNativeAdvancedActorAsync(_me.RequireMe().Id, null, ct);
        var capture = await CaptureNativeAdvancedAsync(request, actor.Me, ct);
        if (capture.Key != request.SnapshotId) throw NativeAdvancedError("ADVANCED_NATIVE_SNAPSHOT_STALE");
        _candidateActivation.RequireCapability(StatRunCapabilities.AdvancedSummary, StatRunRouteRegistry.AdvancedBuild);
        var store = new AdvancedNativeSnapshotStore(_ctx.Db);
        var stored = await store.ReadAsync(request.SnapshotId!, ct)
            ?? throw NativeAdvancedError("ADVANCED_NATIVE_COMPARISON_SNAPSHOT_REQUIRED");
        if (stored.Hash != snapshotHash) throw NativeAdvancedError("ADVANCED_NATIVE_COMPARISON_ARTIFACT_CHANGED");
        var actual = stored.Snapshot;
        if (actual.ActorId != actor.Me.Id || actual.ScopeAssignmentId != capture.Owner.Assignment.Id
            || actual.TemplateId != capture.Owner.Template.Id || actual.SectionId != request.SectionId
            || actual.Grain != request.Grain || actual.GrainKey != request.GrainKey)
            throw NativeAdvancedError("ADVANCED_NATIVE_COMPARISON_SCOPE_MISMATCH");

        // Does not call GetNativeSummary: expected must never be the cached actual.
        var expected = CalculateNativeAdvancedSnapshot(capture, null, ct);
        if (actual.DefinitionJson != expected.DefinitionJson || actual.FormConfigurationJson != expected.FormConfigurationJson
            || actual.AdvancedConfigurationJson != expected.AdvancedConfigurationJson || actual.PlanJson != expected.PlanJson
            || !actual.AssignmentIds.SequenceEqual(expected.AssignmentIds, StringComparer.Ordinal))
            throw NativeAdvancedError("ADVANCED_NATIVE_COMPARISON_CAPTURE_BINDING_MISMATCH");
        static NativeComparisonInput Input(AdvancedNativeSnapshot snapshot)
        {
            var projected = NativeStatisticResultProjection.Project(snapshot.DefinitionJson,
                snapshot.FormConfigurationJson, snapshot.PlanJson, snapshot.Native);
            // Compare the complete stored groups. Display filtering must not hide
            // an unexpected target or operation in a corrupted result.
            return new(new(snapshot.ScopeAssignmentId, snapshot.TemplateId, snapshot.SectionId,
                snapshot.Grain, snapshot.GrainKey, "UTC_GREGORIAN", snapshot.StartUtc, snapshot.EndExclusiveUtc,
                snapshot.Configuration, snapshot.SourceSetHash, projected.Metadata.ContentHash), snapshot.Native);
        }
        var expectedInput = Input(expected); var actualInput = Input(actual);
        var comparison = NativeStatisticSnapshotComparer.Compare(expectedInput, actualInput);
        async Task Revalidate(CancellationToken token)
        {
            var current = await LoadNativeAdvancedActorAsync(actor.Me.Id, actor.Hash, token);
            RequireSameNativeAdvanced(capture, await CaptureNativeAdvancedAsync(request, current.Me, token));
            var reread = await store.ReadAsync(request.SnapshotId!, token);
            if (reread is null || reread.Value.Hash != snapshotHash) throw NativeAdvancedError("ADVANCED_NATIVE_COMPARISON_ARTIFACT_CHANGED");
        }
        await Revalidate(ct);
        await _statConfigTransactions.ExecuteAsync(async (session, token) => {
            RequireNativeAdvancedCandidate(capture);
            await FenceNativeAdvancedAsync(session, capture, token);
            try {
                await StatRunDirectProjectionService.FenceDocumentAsync(session, _ctx.Users, actor.Owner,
                    ["_id", "username", "fullName", "unitId", "roles", "positionCode", "accountKind", "isDeleted"], token);
            }
            catch (InvalidOperationException ex) when (ex.Message == "P9_DIRECT_NATIVE_PUBLICATION_SOURCE_FENCE_STALE")
            { throw NativeAdvancedError("ADVANCED_NATIVE_REFRESH_ACTOR_CHANGED"); }
            var pinned = await store.ReadAsync(request.SnapshotId!, token, session);
            if (pinned is null || pinned.Value.Hash != snapshotHash) throw NativeAdvancedError("ADVANCED_NATIVE_COMPARISON_ARTIFACT_CHANGED");
            return true;
        }, ct);
        // Also catches membership additions, which existing-document CAS alone
        // cannot detect. This is freshness at observation, not a future guarantee.
        await Revalidate(ct);
        return new(actual.SnapshotId, stored.Hash, capture.SourceHash, DateTime.UtcNow, comparison,
            expectedInput.Identity, actualInput.Identity, StatConfigCanonicalJson.HashObject(expected.Native),
            StatConfigCanonicalJson.HashObject(actual.Native), capture.Assignments.Select(a => a.Id).ToArray(),
            new(expected.Native.SchemaHash, expected.Native.PlanContentDigest, expected.Native.SourceOrderDigest, expected.Native.Sources),
            new(actual.Native.SchemaHash, actual.Native.PlanContentDigest, actual.Native.SourceOrderDigest, actual.Native.Sources));
    }
}
