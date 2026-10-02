using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;

var checks = 0;
void Check(bool pass, string name) { if (!pass) throw new Exception(name); checks++; }
async Task Error(Func<Task> run, string code)
{ try { await run(); throw new Exception("Expected " + code); } catch (AggregatePreviewException e) when (e.Code == code) { checks++; } }
var now = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);
AggregateCommandContext Command(string id, string op = "APPLY", string context = "reportB") => new(id, op, context, "B", "sessionB", now);
Check(AggregateCanonical.Hash(new { a = 1, b = 2 }) == AggregateCanonical.Hash(new { b = 2, a = 1 }), "canonical keys sorted");
Check(AggregateCanonical.Hash(new { a = 1, b = (string?)null }) == AggregateCanonical.Hash(new { a = 1 }), "null and absent same");
Check(AggregateCanonical.Hash(new[] { 1, 2 }) != AggregateCanonical.Hash(new[] { 2, 1 }), "array order preserved");
var tokens = new AggregateConfirmationTokens(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
var stamp = new AggregateConfirmation("APPLY", "B", "sessionB", "reportB", "request", "evidence", now.AddMinutes(5));
var signed = tokens.Issue(stamp); tokens.Verify(signed, stamp, now); checks++;
await Error(() => { tokens.Verify(signed, stamp with { Actor = "A" }, now); return Task.CompletedTask; }, "AGG_CONFIRMATION_STALE");
await Error(() => { tokens.Verify(signed, stamp with { SessionKey = "different" }, now); return Task.CompletedTask; }, "AGG_CONFIRMATION_STALE");
await Error(() => { tokens.Verify(signed, stamp, now.AddMinutes(5)); return Task.CompletedTask; }, "AGG_CONFIRMATION_STALE");
await Error(() => { tokens.Verify(signed[..^4] + "AAAA", stamp, now); return Task.CompletedTask; }, "AGG_CONFIRMATION_STALE");

var store = new TestStore(); store.Reports["reportB"] = new();
var reader = new TestReader(store); var service = new AggregateCommandService(store, reader, tokens);
var context = TestReader.Context();
var create = new AggregateConfigCreateRequestDto("create", context.BindingId, TestReader.TargetForm, TestReader.Recipe());
var config = await service.CreateConfigAsync(Command("create", "CREATE_CONFIG"), context, create, default);
Check(config.Revision == 1 && store.Rows.Count == 3, "config head, immutable version and durable receipt");
var created = await service.CreateInstanceAsync(Command("instance", "CREATE_INSTANCE"), context, config.Id, default);
var id = created.Id;
var discovered = await service.ReadContextAsync(Command("discover", "READ"), context, default);
Check(discovered.Config?.Id == config.Id && discovered.InstanceId == id && discovered.Revisions.InstanceRevision == 1,
    "reopening a report discovers persisted identity without client-derived hashes");
store.Reports["reportOtherDraft"] = new();
var otherContext = TestReader.Context("reportOtherDraft") with { BindingId = context.BindingId };
var otherDraft = await service.CreateInstanceAsync(Command("other-instance", "CREATE_INSTANCE", "reportOtherDraft"), otherContext, config.Id, default);
AggregateInstanceState Instance() => store.Value<AggregateInstanceState>(AggregateCollections.Instances, id)!;
AggregateMappingChange Change(AggregateInstanceOverrideDto? overlay = null) => new(Instance().Revision, overlay, Instance().Selection, [], false);
var change = Change(); var preview = await service.PreviewChangeAsync(Command("p"), context, id, change, default);
var writesBeforePreview = store.WriteCount;
await service.PreviewChangeAsync(Command("p"), context, id, change, default);
Check(store.WriteCount == writesBeforePreview, "preview does not write token/cache/DB rows");
Check(preview.Preview.Preview.Results.Single().Value!.Value.GetString() == "30", "P02 evaluator used by P03 preview");
var applied = await service.ApplyAsync(Command("apply1"), context, id, change, preview.Token, default);
Check(store.Reports["reportB"].Value == "30" && applied.PayloadRevision == 2, "apply writes current value once");
Check(Instance().Generation == 2 && Instance().Revision == 2, "apply advances instance and dependency generation");
Check(store.Value<AggregateDependencyState>(AggregateCollections.Dependencies, id)!.Keys.Contains("REPORT:r1"), "reverse report dependency stored");
var replay = await service.ApplyAsync(Command("apply1"), context, id, change, preview.Token, default);
Check(replay.Replayed && store.Reports["reportB"].Payload == 2, "lost-response retry receipt does not double-write");
await Error(() => service.ApplyAsync(Command("apply1"), otherContext, id, change, preview.Token, default), "AGG_COMMAND_PAYLOAD_MISMATCH");
await Error(() => service.ApplyAsync(Command("apply1"), context, id, change with { ResetToPinned = true }, preview.Token, default), "AGG_COMMAND_PAYLOAD_MISMATCH");
reader.Authorized = false;
await Error(() => service.ApplyAsync(Command("apply1"), context, id, change, preview.Token, default), "AGG_CONTEXT_UNAVAILABLE");
reader.Authorized = true;
await Error(() => service.PreviewChangeAsync(Command("p"), context, id, change, default), "AGG_REVISION_CONFLICT");

var version1 = store.Value<AggregateConfigVersion>(AggregateCollections.Versions, AggregateCommandService.VersionKey(config.Id, 1))!;
AggregateExpressionDto Plus(long value) => new() { Kind = "BINARY", Name = "+", Arguments = [TestReader.Sum(), new() { Kind = "NUMBER", Value = value.ToString() }] };
var overlay = new AggregateInstanceOverrideDto(version1.RecipeHash, [], [new("c", "out", Plus(5))], []);
change = Change(overlay); preview = await service.PreviewChangeAsync(Command("p2"), context, id, change, default);
Check(preview.Instance.Applied == null, "mapping preview exposes fresh evidence without the previous source snapshot");
reader.SourceValue = 40; reader.SourceRevision++;
await Error(() => service.ApplyAsync(Command("stale"), context, id, change, preview.Token, default), "AGG_CONFIRMATION_STALE");
reader.SourceValue = 30; reader.SourceRevision--;
store.WriteCount = 0; store.FailAtWrite = 3;
var beforeRows = AggregateCanonical.Hash(store.Rows.Select(p => new { p.Key, p.Value }).ToArray());
try { await service.ApplyAsync(Command("failure"), context, id, change, preview.Token, default); throw new Exception("Expected injected failure"); }
catch (InvalidOperationException e) when (e.Message == "INJECTED_WRITE_FAILURE") { checks++; }
Check(store.Reports["reportB"].Payload == 2 && store.Reports["reportB"].Value == "30", "fault rolls back payload");
Check(AggregateCanonical.Hash(store.Rows.Select(p => new { p.Key, p.Value }).ToArray()) == beforeRows, "fault rolls back metadata and receipt");
store.FailAtWrite = 0;
await service.ApplyAsync(Command("apply2"), context, id, change, preview.Token, default);
Check(store.Reports["reportB"].Value == "35" && Instance().Overrides != null, "per-report overlay without Form clone");

var impact = new AggregateConfigImpactRequestDto(1, TestReader.Recipe(Plus(2)), [new(id, Instance().Revision)], []);
var impactPreview = await service.PreviewConfigAsync(Command("r2preview", "CONFIG_REVISION"), context, config.Id, impact, default);
Check(impactPreview.Plan.Conflicts.Count == 1 && impactPreview.Token == null, "r2 conflicting overlay needs explicit resolution");
Check(impactPreview.Plan.Instances.All(i => i.Applied == null), "conflict-only impact withholds historical child evidence");
Check(!System.Text.Json.JsonSerializer.Serialize(impactPreview, AggregateCanonical.Json).Contains("sessionB", StringComparison.Ordinal), "impact response never serializes server authority/session capture");
impact = impact with { Resolutions = [new(id, "c", "out", null, "KEEP_OVERRIDE")] };
impactPreview = await service.PreviewConfigAsync(Command("r2preview", "CONFIG_REVISION"), context, config.Id, impact, default);
await service.SaveConfigAsync(Command("r2save", "CONFIG_REVISION"), context, config.Id, impact, impactPreview.Token!, default);
Check(Instance().ConfigRevision == 2 && store.Reports["reportB"].Value == "35", "selected Draft r2 preserves chosen override");
Check(store.Value<AggregateConfigHead>(AggregateCollections.Configs, config.Id)!.HeadRevision == 2, "head published atomically with selected Draft");
Check(store.Value<AggregateInstanceState>(AggregateCollections.Instances, otherDraft.Id)!.ConfigRevision == 1, "unselected existing Draft stays on r1");
var beforeReadWrites = store.WriteCount;
var pinnedRead = System.Text.Json.JsonSerializer.SerializeToElement(
    await service.ReadConfigAsync(Command("read-r1", "READ"), context, config.Id, default, 1), AggregateCanonical.Json);
Check(pinnedRead.GetProperty("head").GetProperty("headRevision").GetInt64() == 2
    && pinnedRead.GetProperty("version").GetProperty("revision").GetInt64() == 1, "editor reads exact pinned r1 while head is r2");
Check(store.WriteCount == beforeReadWrites, "read pinned config never migrates or writes an instance");
await Error(() => service.ReadConfigAsync(Command("read-future", "READ"), context, config.Id, default, 3), "AGG_REVISION_CONFLICT");
await Error(() => service.ReadConfigAsync(Command("read-zero", "READ"), context, config.Id, default, 0), "AGG_REVISION_CONFLICT");
store.Reports["reportNewPeriod"] = new();
var newPeriodContext = TestReader.Context("reportNewPeriod") with { BindingId = context.BindingId };
var newPeriod = await service.CreateInstanceAsync(Command("new-period", "CREATE_INSTANCE", "reportNewPeriod"), newPeriodContext, config.Id, default);
Check(store.Value<AggregateInstanceState>(AggregateCollections.Instances, newPeriod.Id)!.ConfigRevision == 2, "new period pins r2 after head commit");
Check(store.Value<AggregateConfigVersion>(AggregateCollections.Versions, AggregateCommandService.VersionKey(config.Id, 1))!.RecipeHash == version1.RecipeHash, "r1 immutable history");
var reset = Change() with { ResetToPinned = true };
preview = await service.PreviewChangeAsync(Command("reset"), context, id, reset, default);
await service.ApplyAsync(Command("reset"), context, id, reset, preview.Token, default);
Check(Instance().ConfigRevision == 2 && Instance().Overrides == null && store.Reports["reportB"].Value == "32", "reset preview returns to pinned base");

var lifecycle = new AggregateLifecycleParticipant(store, reader, tokens);
async Task Transition(string reportId, string from, string to, string submission, string? confirmation, string? draftOwner = null)
{
    var ctx = TestReader.Context(reportId); var authority = await reader.AuthorizeAsync(ctx, "B", "sessionB", default);
    var r = store.Reports[reportId];
    var resultLifecycle = r.Lifecycle + (from == "Draft" && to == "Approved" ? 2 : 1);
    await store.ExecuteAsync(async (tx, ct) =>
    {
        await lifecycle.BeforeCommitAsync(tx, Command("life-" + r.Lifecycle, "LIFECYCLE", reportId), authority,
            new(reportId, "EXISTING_OWNER", from, to, r.Payload, r.Lifecycle, resultLifecycle, submission, true, true, draftOwner), confirmation, ct);
        ((TestStore.Transaction)tx).Reports[reportId] = r with { Status = to, Lifecycle = resultLifecycle };
        return true;
    }, default);
}
reader.Missing = true;
var submit = await lifecycle.PreviewSubmissionAsync(Command("submitpreview", "SUBMIT"), context, default);
Check(submit.Instances.Single().Preview.Completeness == "MISSING_SOURCES", "missing slots disclosed but not a submit gate");
await Transition("reportB", "Draft", "Approved", "submission1", submit.Token);
Check(Instance().State == "FROZEN" && store.Reports["reportB"].Status == "Approved", "actual auto-approved transition freezes snapshot");
await Error(() => service.PreviewChangeAsync(Command("frozen-edit"), context, id, Change(), default), "AGG_TARGET_NOT_DRAFT");
Check(store.Value<AggregateLockState>(AggregateCollections.Locks, "REPORT:r1")!.Owners.Count == 1, "linked source lock owner acquired");
Check(store.Value<AggregateLockState>(AggregateCollections.Locks, "REPORT:r1")!.Owners.Single().SubmissionLifecycleRevision == 2
    && store.Reports["reportB"].Lifecycle == 3, "two-step autoapproval pins owner to submission revision");
Check(store.Value<AggregateLockState>(AggregateCollections.Locks, "SLOT:bindingD:20260930")!.Owners.Count == 1, "missing slot locked without fake report");
foreach (var operation in AggregateMutationParticipant.ReportOperations)
    await Error(() => store.ExecuteAsync(async (tx, ct) =>
    {
        await AggregateMutationParticipant.BeforeReportWriteAsync(tx, new("work", "C", "r1", "bindingC", "20260930", operation, [], true), ct);
        return true;
    }, default), "AGG_SOURCE_LOCKED");
await Error(() => store.ExecuteAsync(async (tx, ct) => { await AggregateMutationParticipant.BeforeRelationshipWriteAsync(tx, "work", ["C"], [], true, ct); return true; }, default), "AGG_SOURCE_LOCKED");
await Error(() => store.ExecuteAsync(async (tx, ct) => { await AggregateMutationParticipant.BeforeRelationshipWriteAsync(tx, "work", [], ["bindingD"], true, ct); return true; }, default), "AGG_SOURCE_LOCKED");
await Error(() => { AggregateMutationParticipant.EnsureParentCompletion(true, [true, false]); return Task.CompletedTask; }, "AGG_ASSIGNMENT_CHILDREN_PENDING");
AggregateMutationParticipant.EnsureParentCompletion(true, [true, true]); checks++;
AggregateMutationParticipant.EnsureParentCompletion(true, []); checks++;
await Error(() => store.ExecuteAsync(async (tx, ct) => { await AggregateLifecycleParticipant.EnsureMutationAsync(tx, "r1", "bindingC", "20260930", ct); return true; }, default), "AGG_SOURCE_LOCKED");
await Error(() => store.ExecuteAsync(async (tx, ct) => { await AggregateLifecycleParticipant.EnsureMutationAsync(tx, "", "bindingD", "20260930", ct); return true; }, default), "AGG_SOURCE_LOCKED");
await store.ExecuteAsync(async (tx, ct) => { await AggregateLifecycleParticipant.EnsureMutationAsync(tx, "", "bindingD", "20261031", ct); return true; }, default); checks++;
var frozen = AggregateCanonical.Hash(store.Value<AggregateFrozenState>(AggregateCollections.Frozen, AggregateCanonical.Key("submission1", id)));
// A second report in the same owning assignment holds the same source independently.
var otherState = store.Value<AggregateInstanceState>(AggregateCollections.Instances, otherDraft.Id)!;
var otherChange = new AggregateMappingChange(otherState.Revision, null, otherState.Selection, [], false);
var otherPreview = await service.PreviewChangeAsync(Command("other-apply", context: "reportOtherDraft"), otherContext, otherDraft.Id, otherChange, default);
await service.ApplyAsync(Command("other-apply", context: "reportOtherDraft"), otherContext, otherDraft.Id, otherChange, otherPreview.Token, default);
var otherSubmit = await lifecycle.PreviewSubmissionAsync(Command("other-submit", "SUBMIT", "reportOtherDraft"), otherContext, default);
var otherAuthority = await reader.AuthorizeAsync(otherContext, "B", "sessionB", default);
await store.ExecuteAsync(async (tx, ct) =>
{
    var report = store.Reports["reportOtherDraft"];
    await lifecycle.BeforeCommitAsync(tx, Command("other-life", "LIFECYCLE", "reportOtherDraft"), otherAuthority,
        new("reportOtherDraft", "SUBMIT", "Draft", "Submitted", report.Payload, report.Lifecycle, report.Lifecycle + 1, "submission2", true, true), otherSubmit.Token, ct);
    ((TestStore.Transaction)tx).Reports["reportOtherDraft"] = report with { Status = "Submitted", Lifecycle = report.Lifecycle + 1 };
    return true;
}, default);
Check(store.Value<AggregateLockState>(AggregateCollections.Locks, "REPORT:r1")!.Owners.Count == 2, "same source has two independent submission owners");
await Transition("reportB", "Approved", "Submitted", "submission1", null);
Check(store.Value<AggregateLockState>(AggregateCollections.Locks, "REPORT:r1")!.Owners.Count == 2, "recall retains frozen owners");
await Transition("reportB", "Submitted", "Draft", "submission1", null);
Check(Instance().State == "DRAFT" && store.Value<AggregateLockState>(AggregateCollections.Locks, "REPORT:r1")!.Owners.Single().SubmissionId == "submission2", "return releases exact owner and keeps other owner");
await Error(() => store.ExecuteAsync(async (tx, ct) => { await AggregateLifecycleParticipant.EnsureMutationAsync(tx, "r1", "bindingC", "20260930", ct); return true; }, default), "AGG_SOURCE_LOCKED");
Check(AggregateCanonical.Hash(store.Value<AggregateFrozenState>(AggregateCollections.Frozen, AggregateCanonical.Key("submission1", id))) == frozen, "return preserves immutable frozen history");

var generation = Instance().Generation;
await store.ExecuteAsync(async (tx, ct) => { await AggregateRefreshService.InvalidateAsync(tx, "work", ["REPORT:r1"], "event1", ct); return true; }, default);
var refresh = new AggregateRefreshService(store, reader);
Check(await refresh.RunAsync(id, generation - 1, default) == "NO_WORK", "old generation job does not write after return");
// Reapply establishes dependencies for the new Draft generation.
change = Change(); preview = await service.PreviewChangeAsync(Command("reapply"), context, id, change, default);
await service.ApplyAsync(Command("reapply"), context, id, change, preview.Token, default);
generation = Instance().Generation;
reader.SourceValue = 35; reader.SourceRevision++;
await store.ExecuteAsync(async (tx, ct) => { await AggregateRefreshService.InvalidateAsync(tx, "work", ["REPORT:r1"], "event2", ct); return true; }, default);
Check(await refresh.RunAsync(id, generation, default) == "COMPLETED" && store.Reports["reportB"].Value == "37", "refresh recomputes latest input and formula");
var revisionAfterRefresh = store.Reports["reportB"].Payload;
Check(await refresh.RunAsync(id, generation, default) == "NO_WORK" && store.Reports["reportB"].Payload == revisionAfterRefresh, "duplicate job does not repeat write");
await store.ExecuteAsync(async (tx, ct) => { await AggregateRefreshService.InvalidateAsync(tx, "work", ["REPORT:r1"], "event2", ct); return true; }, default);
Check(await refresh.RunAsync(id, generation, default) == "NO_WORK", "replayed completed event does not enqueue another write");
var unlinkRevision = Instance().Revision;
var unlinkToken = await service.PreviewUnlinkAsync(Command("unlink", "UNLINK"), context, id, unlinkRevision, default);
await service.UnlinkAllAsync(Command("unlink", "UNLINK"), context, id, unlinkRevision, unlinkToken, default);
Check(Instance().State == "UNLINKED" && store.Reports["reportB"].Value == "37" && store.Reports["reportB"].Payload == revisionAfterRefresh, "confirmed unlink keeps numbers manual");
Check(store.Value<AggregateDependencyState>(AggregateCollections.Dependencies, id) == null, "unlink removes dependency");
Check(!store.Rows.Any(p => p.Key.Collection == AggregateCollections.Targets && p.Value.Target == "reportB"), "unlink releases target ownership");
var partial = TestReader.Recipe();
partial.Nodes[1].Outputs.Add(new("out2", "NUMBER", "SINGLE"));
partial.Nodes[1].Expressions!.Add(new("out2", TestReader.Sum()));
partial.Nodes[2].Inputs.Add(new("in2", "NUMBER", "SINGLE", "second"));
partial.Edges.Add(new("e3", new("c", "out2"), new("t", "in2")));
partial = AggregateOverlay.Validate(partial);
var pruned = AggregateOverlay.Prune(partial, ["total"]);
Check(pruned.Nodes.Single(n => n.Kind == "TARGET").Inputs.Single().MemberId == "second", "partial unlink only removes selected target");
Check(pruned.Nodes.Any(n => n.Kind == "SOURCE") && pruned.Nodes.Single(n => n.Kind == "CALCULATION").Expressions!.Single().PortId == "out2", "shared source dependency survives other target mapping");
partial.Nodes[0].Outputs[0] = partial.Nodes[0].Outputs[0] with { ValueType = "TABLE" };
partial.Nodes[1].Inputs[0] = partial.Nodes[1].Inputs[0] with { ValueType = "TABLE" };
partial.Nodes[1].Outputs[1] = partial.Nodes[1].Outputs[1] with { ValueType = "TABLE" };
partial.Nodes[2].Inputs[1] = partial.Nodes[2].Inputs[1] with { ValueType = "TABLE" };
partial.Nodes[1].Expressions![1] = new("out2", new() { Kind = "CALL", Name = "APPEND_TABLE", Arguments = [new() { Kind = "TABLE_FILTER", Ref = partial.Nodes[1].Inputs[0].Id,
    Area = new("ALL", null, null, null, null), ColumnIndex = 1, Predicate = new() { Kind = "CALL", Name = "IS_PRESENT", Arguments = [new() { Kind = "INPUT", Ref = "cell" }] } }] });
var tablePruned = AggregateOverlay.Prune(partial, ["total"]);
Check(tablePruned.Nodes.Any(n => n.Kind == "SOURCE") && tablePruned.Nodes.Single(n => n.Kind == "CALCULATION").Inputs.Count == 1,
    "partial unlink retains TABLE_FILTER source dependency without treating local cell as a graph port");
var declaration = new AggregateDataWindowDeclarationDto("2026-09-01", "2026-09-30", "USER_DECLARED", "B declares September", 1);
var dateToken = await service.PreviewDeclarationAsync(Command("date", "DATA_WINDOW"), context, declaration, default);
await service.SaveDeclarationAsync(Command("date", "DATA_WINDOW"), context, declaration, dateToken, default);
Check(store.Value<AggregateDeclarationState>(AggregateCollections.Declarations, "REPORT:reportB")!.Declaration.StartDate == "2026-09-01", "declared report dates persisted separately from deadline");
Check(store.Value<AggregateDeclarationState>(AggregateCollections.Declarations, "SLOT:" + context.BindingId + ":" + context.PeriodKey) != null, "same command pins current occurrence data declaration");
await Error(() => service.PreviewDeclarationAsync(Command("date", "DATA_WINDOW"), context, declaration with { ProvenanceKind = "VERIFIED_FORM_DATE_FIELDS" }, default), "AGG_DATA_WINDOW_UNRESOLVED");
var rawRevision = Instance().Revision;
await service.SaveRawDraftAsync(Command("raw", "RAW_DRAFT"), context, id, rawRevision, "{broken", default);
Check(Instance().RawDraft!.RawJson == "{broken" && store.Reports["reportB"].Value == "37", "invalid raw draft preserves existing numbers");
var unsupportedChange = new AggregateMappingChange(Instance().Revision, new("wrong-base", [], [], []), Instance().Selection, [], false);
await Error(() => service.PreviewChangeAsync(Command("badbase"), context, id, unsupportedChange, default), "AGG_OVERRIDE_BASE_STALE");
var refreshFrozenBefore = store.Reports["reportOtherDraft"];
await store.ExecuteAsync(async (tx, ct) => { await AggregateRefreshService.InvalidateAsync(tx, "work", ["REPORT:r1"], "late-event", ct); return true; }, default);
Check(store.Reports["reportOtherDraft"] == refreshFrozenBefore, "invalidation does not write frozen payload/status");
Check(!store.Rows.Any(p => p.Key.Collection == AggregateCollections.Refresh && p.Value.Target == "reportOtherDraft" && p.Value.Keys.Contains("PENDING")), "frozen report does not enqueue new refresh work");
// Integration-domain regressions: old source evidence, approved return, missing occurrence,
// readonly native target members and no historical child values in mapping readback.
var savedEvidence = store.Value<AggregateFrozenState>(AggregateCollections.Frozen, AggregateCanonical.Key("submission1", id))!;
Check(savedEvidence.Preview.SourceValues.Single().Value.GetProperty("numerator").GetString() == "30", "frozen source evidence survives later child edits");
Check(savedEvidence.Preview.SourceValues.Single().Trace.Single().ReportId == "r1", "source evidence preserves selected report identity");
await Transition("reportOtherDraft", "Submitted", "Approved", "submission2", null);
await Transition("reportOtherDraft", "Approved", "Draft", "submission2", null, "new-assignee");
Check(store.Value<AggregateLockState>(AggregateCollections.Locks, "REPORT:r1")!.Owners.Count == 0, "approved return releases only the final owner without cascading to source");
var returnedOther = store.Value<AggregateInstanceState>(AggregateCollections.Instances, otherDraft.Id)!;
Check(returnedOther.AuthorityUserId == "new-assignee", "returned snapshot refresh uses current handover principal");
Check(store.Value<AggregateFrozenState>(AggregateCollections.Frozen, AggregateCanonical.Key("submission2", otherDraft.Id))!.Instance.AuthorityUserId == "B", "handover preserves frozen historical author");
var readback = await service.ReadInstanceAsync(Command("read", "READ"), context, id, default);
Check(readback.ResultFreshness == "NEEDS_REPAIR" && readback.Instance.Applied == null && readback.LastResults.Count == 1,
    "readback marks old values and withholds historical child lineage until an authorized preview");
var historicalTable = new AggregateTargetResultDto("t", "p", "TABLE", "RESULT",
    System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"rows\":[{\"cells\":[{\"value\":\"30\",\"lineage\":[{\"reportId\":\"private-child\"}]}]}]}"),
    null, null, "old-source-lineage", null);
var visibleTable = AggregateCommandService.WithoutSourceLineage(historicalTable);
Check(visibleTable.LineageRef == "" && !visibleTable.Value!.Value.GetRawText().Contains("private-child", StringComparison.Ordinal)
    && visibleTable.Value.Value.GetProperty("rows")[0].GetProperty("cells")[0].GetProperty("value").GetString() == "30",
    "saved Table readback keeps target value without nested child trace");

var slotContext = context with { ReportId = null, WorkReportPeriodId = null, PeriodInstanceKey = null, PeriodKey = "20261031" };
var reportsBeforeSlot = store.Reports.Count;
var discoveredSlot = await service.ReadContextAsync(Command("discover-slot", "READ"), slotContext, default);
Check(discoveredSlot.Config?.Id == config.Id && discoveredSlot.InstanceId == null, "missing occurrence discovers shared config without creating an instance");
var slotDate = declaration with { StartDate = "2026-10-01", EndDate = "2026-10-31" };
var slotToken = await service.PreviewDeclarationAsync(Command("slot-date", "DATA_WINDOW", "slot-oct"), slotContext, slotDate, default);
await service.SaveDeclarationAsync(Command("slot-date", "DATA_WINDOW", "slot-oct"), slotContext, slotDate, slotToken, default);
Check(store.Reports.Count == reportsBeforeSlot, "missing occurrence declaration never materializes a report");
await Error(() => service.SaveDeclarationAsync(Command("slot-date", "DATA_WINDOW", "slot-oct"),
    slotContext with { PeriodKey = "20261130" }, slotDate, slotToken, default), "AGG_COMMAND_PAYLOAD_MISMATCH");
Check(store.Value<AggregateDeclarationState>(AggregateCollections.Declarations, "SLOT:" + context.BindingId + ":20261031")!.Declaration.StartDate == "2026-10-01",
    "missing occurrence uses explicit declared data dates");
await Error(() => service.CreateInstanceAsync(Command("no-report"), slotContext, config.Id, default), "AGG_REPORT_REQUIRED");
await Error(() => service.PreviewDeclarationAsync(Command("stale-slot", "DATA_WINDOW"), slotContext, slotDate, default), "AGG_REVISION_CONFLICT");
var onceContext = slotContext with { Kind = "ONCE", BindingId = "binding-once", PeriodKey = "20260930" };
var onceToken = await service.PreviewDeclarationAsync(Command("once-date", "DATA_WINDOW", "once"), onceContext, declaration, default);
await service.SaveDeclarationAsync(Command("once-date", "DATA_WINDOW", "once"), onceContext, declaration, onceToken, default);
Check(store.Value<AggregateDeclarationState>(AggregateCollections.Declarations, "SLOT:binding-once:ONCE") != null,
    "one-off occurrence has one canonical identity regardless of deadline day key");

AggregateMappedMembers.EnsureUnchanged(["total"], "{\"values\":{\"total\":30,\"manual\":1}}", null,
    "{\"values\":{\"manual\":2,\"total\":30}}", null); checks++;
await Error(() => { AggregateMappedMembers.EnsureUnchanged(["total"], "{\"total\":30}", null, "{\"total\":31}", null); return Task.CompletedTask; }, "AGG_TARGET_MAPPED_READ_ONLY");
await Error(() => { AggregateMappedMembers.EnsureUnchanged(["total"], "{\"total\":30}", null, null, null); return Task.CompletedTask; }, "AGG_TARGET_MAPPED_READ_ONLY");
await Error(() => { AggregateMappedMembers.EnsureUnchanged(["table"], null, "{\"nativeTables\":{\"table\":{\"rows\":[1,2]}}}", null,
    "{\"nativeTables\":{\"table\":{\"rows\":[2,1]}}}"); return Task.CompletedTask; }, "AGG_TARGET_MAPPED_READ_ONLY");
Check(AggregateMappingPolicy.Decide(AggregateAction.Return, otherAuthority.Read.Authority with { TargetStatus = "Approved", CanReviewReport = true }).Allowed,
    "approved return capability requires reviewer");
Check(!AggregateMappingPolicy.Decide(AggregateAction.Return, otherAuthority.Read.Authority with { TargetStatus = "Approved", CanReviewReport = false }).Allowed,
    "approved return never becomes assignee self-return");
var editClaims = new AggregateTargetClaim[] { new("instance-b","report","table"), new("instance-a","report","field") };
var hint = AggregateReportEditHints.Build("report","actor",4,2,"schema",editClaims);
Check(hint.ReadOnlyMemberIds.SequenceEqual(["field","table"])&&hint.ActorId=="actor"&&hint.ReportId=="report"&&hint.PayloadRevision==4&&hint.LifecycleRevision==2,"edit hint is tied to authorized actor/report/schema/revisions and exact target members");
Check(hint.ClaimDigest==AggregateReportEditHints.Build("report","actor",4,2,"schema",editClaims.Reverse().ToArray()).ClaimDigest,"claim digest ignores query order");
Check(AggregateReportEditHints.Build("report","actor",4,2,"schema",[]).ReadOnlyMemberIds.Count==0,"no target claims means no aggregate member locks");
await Error(()=>{AggregateReportEditHints.Build("report","actor",4,2,"schema",[new("i","other","field")]);return Task.CompletedTask;},"AGG_TARGET_CLAIM_INVALID");
await Error(()=>{AggregateReportEditHints.Build("report","actor",4,2,"schema",Enumerable.Repeat(editClaims[0],1001).ToArray());return Task.CompletedTask;},"AGG_BUDGET_EXCEEDED");
Console.WriteLine($"PASS: {checks} P03 in-memory checks; no Mongo/API/job host executed.");
