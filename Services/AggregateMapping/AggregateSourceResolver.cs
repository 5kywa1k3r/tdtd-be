using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

internal sealed class AggregateSourceResolver(IAggregatePreviewReader reader, IAggregateContentSink? contentSink = null, IAggregateListSink? listSink = null)
{
    internal async Task<AggregateResolvedSource> ResolveAsync(AggregateNodeDto node, AggregateRecipeDto recipe,
        AggregateSourceSelectionDto selection, AggregateReadContext context, string actor, AggregateBudget budget, CancellationToken ct,
        Func<AggregateResolvedSource, int, int, Task>? progress = null)
    {
        // T persistence/read adapter is a P03/B12 dependency, never fabricated from a report.
        if (node.Origin != "DIRECT_CHILD_REPORTS") throw new AggregatePreviewException("AGG_INTERMEDIATE_NOT_AVAILABLE");
        if (selection.Mode is not ("FORM_SELECTOR" or "EXPLICIT_REPORTS") || selection.ReportIds == null || selection.ExcludedReportIds == null
            || selection.ReportIds.Distinct().Count() != selection.ReportIds.Count || selection.ExcludedReportIds.Distinct().Count() != selection.ExcludedReportIds.Count
            || (selection.Mode == "FORM_SELECTOR" && selection.ReportIds.Count != 0)
            || selection.ReportIds.Intersect(selection.ExcludedReportIds).Any()) throw new AggregatePreviewException("AGG_SOURCE_SELECTION_INVALID");
        var schema = await reader.ReadSchemaAsync(node.Form!, context, actor, ct);
        foreach (var port in node.Outputs)
            if (!schema.Members.TryGetValue(port.MemberId!, out var member) || member.Type != port.ValueType)
                throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE", "$.nodes[" + node.Id + "]");
        var rules = node.Outputs.ToDictionary(p => p.Id, p => recipe.TimeRules.Single(r => r.Id == p.TimeRuleId));
        var reportSetMode = rules.Values.All(r => r.Mode == AggregateReportSetFilter.Mode);
        var windows = rules.Where(p => p.Value.Mode != AggregateReportSetFilter.Mode).ToDictionary(p => p.Key, p => AggregateTimeResolver.Resolve(p.Value, context));
        var listing = reportSetMode
            ? await reader.ListReportSetSourcesAsync(context, schema.Pin, actor, ct)
            : await reader.ListSourcesAsync(context, schema.Pin, actor, ct);
        if (!listing.Complete) throw new AggregatePreviewException("AGG_SOURCE_ENUMERATION_INCOMPLETE");
        if (listing.Headers.Count > 10_000 || listing.Slots.Count > 20_000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        var candidates = listing.Headers.Where(h => h.Form == schema.Pin && h.Pin.IsCurrent && !h.Deleted).ToArray();
        if (candidates.GroupBy(h => (h.Pin.BindingId, h.OccurrenceKey)).Any(g => g.Count() > 1))
            throw new AggregatePreviewException("AGG_CURRENT_REPORT_AMBIGUOUS");
        var available = candidates.Select(h => h.Pin.ReportId).ToHashSet(StringComparer.Ordinal);
        var missingIds = selection.ReportIds.Concat(selection.ExcludedReportIds).Where(id => !available.Contains(id)).Distinct().ToArray();
        var inactive = missingIds.Length == 0 ? [] : await reader.ReadInactiveSourcesAsync(context, schema.Pin, missingIds, actor, ct);
        foreach (var header in inactive)
        {
            if (!header.WholeReportReadable || header.Active || header.Deleted || header.Pin.IsCurrent || header.Form != schema.Pin
                || header.ParentAssignmentId != AggregateTargetIdentity.Parent(context.Context) || header.Pin.WorkId != context.Context.WorkId)
                throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
            available.Add(header.Pin.ReportId);
        }
        if (selection.ReportIds.Concat(selection.ExcludedReportIds).Any(id => !available.Contains(id)))
            throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
        var outputs = node.Outputs.ToDictionary(p => p.Id, _ => new List<AggregateObservation>());
        bool CanSpool(AggregatePortDto port)
        {
            if(contentSink==null||port.ValueType!="TEXT"||port.Shape!="SET")return false;
            var edges=recipe.Edges.Where(e=>e.From.NodeId==node.Id&&e.From.PortId==port.Id).ToArray();
            return edges.Length>0&&edges.All(edge=>{
                var target=recipe.Nodes.Single(n=>n.Id==edge.To.NodeId);
                return target.Kind=="CALCULATION"&&target.TableAssignments is not {Count:>0}
                    && target.Expressions is {Count:>0} && target.Expressions.All(e=>e.Expression.Kind=="CALL"&&e.Expression.Name=="REPORT_TEXT_TABLE"
                        &&e.Expression.Arguments is {Count:1}&&e.Expression.Arguments[0].Kind=="INPUT"&&e.Expression.Arguments[0].Ref==edge.To.PortId);
            });
        }
        var spooled=node.Outputs.Where(CanSpool).Select(p=>p.Id).ToHashSet(StringComparer.Ordinal);
        bool CanStageList(AggregatePortDto port)
        {
            if (listSink == null || port.ValueType != "LIST") return false;
            var edges = recipe.Edges.Where(e => e.From.NodeId == node.Id && e.From.PortId == port.Id).ToArray();
            return edges.Length > 0 && edges.All(edge => {
                var calculation = recipe.Nodes.Single(n => n.Id == edge.To.NodeId);
                if (calculation.Kind == "FILTER" && calculation.Predicate?.Kind == "LIST_PIPELINE") return true;
                bool Safe(AggregateExpressionDto e) => !(e.Kind == "INPUT" && e.Ref == edge.To.PortId)
                    && (e.Arguments?.All(Safe) ?? true) && (e.Predicate == null || Safe(e.Predicate));
                return calculation.Kind == "CALCULATION" && calculation.TableAssignments is not { Count: > 0 }
                    && calculation.Expressions is { Count: > 0 } && calculation.Expressions.All(e => Safe(e.Expression));
            });
        }
        var stagedLists = node.Outputs.Where(CanStageList).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var linked = new Dictionary<string, AggregateSourcePinDto>();
        var eligible = new Dictionary<string, AggregateSourcePinDto>();
        var issues = new List<AggregateIssueDto>();
        if (inactive.Any(h => selection.ReportIds.Contains(h.Pin.ReportId)))
            issues.Add(new("AGG_SOURCE_INACTIVE", "$.nodes[" + node.Id + "]", "Selected hidden reports are retained in the configuration but do not contribute."));
        var ordered = candidates.OrderBy(h => h.UnitId, StringComparer.Ordinal).ThenBy(h => h.OccurrenceKey, StringComparer.Ordinal).ThenBy(h => h.Pin.ReportId, StringComparer.Ordinal).ToArray();
        var positions = ordered.Select((h, i) => (h.Pin.ReportId, i)).ToDictionary(p => p.ReportId, p => p.i, StringComparer.Ordinal);
        var preparedThrough = -1;
        var coverage = node.Outputs.Where(p => windows.ContainsKey(p.Id)).SelectMany(p => AggregateCoverageResolver.Resolve(listing, schema.Pin, [windows[p.Id]], budget, rules[p.Id].ReportFilter))
            .GroupBy(c => c.SlotKey).Select(g => g.FirstOrDefault(c => c.State == "UNKNOWN") ?? g.First()).ToArray();
        AggregateChannel Channel(AggregatePortDto p) => new(p.ValueType, p.Shape, outputs[p.Id].ToArray(), p.ValueType == "TABLE", schema.Members[p.MemberId!].List)
            { EligibleSources = outputs[p.Id].SelectMany(o => o.Trace).Distinct().ToArray(),
                ChoiceOptions = schema.Members[p.MemberId!].ChoiceOptions };
        AggregateResolvedSource Capture() => new(node, schema, node.Outputs.ToDictionary(p => p.Id, Channel),
            linked.Values.ToArray(), eligible.Values.ToArray(), coverage, windows.Values.Distinct().ToArray(), issues.ToArray(),
            listing.MembershipRevision, listing.CurrentUnitIds, !coverage.Any(c => c.State == "UNKNOWN"));
        if (progress != null) await progress(Capture(), 0, ordered.Length);
        for (var sourceIndex = 0; sourceIndex < ordered.Length; sourceIndex++)
        {
            var failed = false;
            try
            {
            var header = ordered[sourceIndex];
            budget.Spend();
            if (selection.ExcludedReportIds.Contains(header.Pin.ReportId) || (selection.Mode == "EXPLICIT_REPORTS" && !selection.ReportIds.Contains(header.Pin.ReportId))) continue;
            if (!header.WholeReportReadable || header.ParentAssignmentId != AggregateTargetIdentity.Parent(context.Context) || header.Pin.WorkId != context.Context.WorkId)
                throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
            if (reportSetMode && (header.Pin.Status != "Approved" || !header.Active))
            {
                // Explicitly linked current reports retain their existing lock identity,
                // even while awaiting approval. They never contribute or load a payload.
                if (header.Active && selection.Mode == "EXPLICIT_REPORTS") linked.TryAdd(header.Pin.ReportId, header.Pin);
                continue;
            }
            var metadataMatching = node.Outputs.Where(p => {
                if (rules[p.Id].Mode != AggregateReportSetFilter.Mode)
                    return AggregateReportMetadataFilter.Matches(rules[p.Id].ReportFilter, header);
                var match = AggregateReportSetFilter.Matches(rules[p.Id].ReportSet!, header, context);
                if (match == null) throw new AggregatePreviewException("AGG_METADATA_DATE_UNAVAILABLE", "$.timeRules[" + rules[p.Id].Id + "]");
                return match.Value;
            }).ToArray();
            if (metadataMatching.Length == 0)
            {
                if (selection.Mode == "EXPLICIT_REPORTS") linked.TryAdd(header.Pin.ReportId, header.Pin);
                continue;
            }
            AggregatePayload? payload = null;
            if (metadataMatching.Any(p => rules[p.Id].Mode != AggregateReportSetFilter.Mode && rules[p.Id].SourceDateBasis != "DECLARED_DATA_WINDOW"))
                payload = await Read(header);
            var matching = metadataMatching.Where(p => rules[p.Id].Mode == AggregateReportSetFilter.Mode || AggregateTimeResolver.MatchesSource(rules[p.Id], windows[p.Id], header, payload)).ToArray();
            if (matching.Length == 0 && selection.Mode != "EXPLICIT_REPORTS") continue;
            linked.TryAdd(header.Pin.ReportId, header.Pin);
            if (header.Pin.Status != "Approved" || !header.Active || matching.Length == 0) continue;
            payload ??= await Read(header);
            eligible.TryAdd(header.Pin.ReportId, header.Pin);
            foreach (var port in matching)
            {
                if (!payload.Values.TryGetValue(port.MemberId!, out var value)) value = AggregateValue.Blank(port.ValueType);
                if (value.Type != port.ValueType || value.State is not ("VALUE" or "BLANK"))
                    throw new AggregatePreviewException("AGG_SOURCE_VALUE_INVALID");
                if (value.List is { } list)
                {
                    var slot = AggregateDigest.Of(new { NodeId = node.Id, PortId = port.Id });
                    value = value with { List = list with { Records = list.Records.Select(row => row with {
                        Key = AggregateDigest.Of(new { Slot = slot, header.Pin.ReportId, row.Origin.ListId, row.Origin.RecordId }),
                        Origin = row.Origin with { SourceSlot = slot, SourceNodeId = node.Id, SourcePortId = port.Id },
                        Cells = row.Cells.ToDictionary(p => p.Key, p => p.Value with {
                            Trace = p.Value.Trace.Select(t => t with { SourceSlot = slot }).ToArray()
                        }, StringComparer.Ordinal)
                    }).ToArray() } };
                }
                var observation=new AggregateObservation(value, [new(header.Pin.ReportId, header.UnitId, header.OccurrenceKey, port.MemberId!,
                    SourceSlot: port.ValueType == "LIST" ? AggregateDigest.Of(new { NodeId = node.Id, PortId = port.Id }) : null)])
                {
                    SourceNote = new("", AggregateDigest.Of(new { header.Pin.BindingId, header.OccurrenceKey, port.MemberId }),
                        header.Pin.ReportId, header.UnitId, header.UnitName ?? "", header.ReportTitle,
                        header.OccurrenceKey, port.MemberId!, header.IsHistoricalData ? header.CompletedDate?.ToString("yyyy-MM-dd") : null,
                        header.IsHistoricalData ? null : header.SubmittedAtUtc?.ToString("O")) {
                            UnitFullName = header.UnitName, UnitShortName = header.UnitShortName, UnitSymbol = header.UnitSymbol }
                };
                if(spooled.Contains(port.Id))
                {
                    // Only direct content-table inputs are staged: predicates/arithmetic must still see real values.
                    // Each source payload is released before the next report is read; retained channels contain references.
                    var one=AggregateEvaluator.Scalar(AggregateContentTable.Build(new("TEXT","SET",[observation]),
                        new(){Order="UNIT_THEN_PERIOD"},budget)).Value.Table!;
                    var reference=await contentSink!.CaptureAsync(context,one,ct);
                    observation=observation with{Value=value with{Text=null,ContentReference=reference}};
                    budget.Spend(bytes:1024);
                }
                if (stagedLists.Contains(port.Id))
                {
                    if (value.List == null) throw new AggregatePreviewException("AGG_LIST_SOURCE_UNAVAILABLE");
                    var reference = await listSink!.CaptureAsync(context, value.List, ct);
                    observation = observation with { Value = value with { List = null, ListReference = reference } };
                    budget.Spend(bytes: 1024);
                }
                outputs[port.Id].Add(observation);
            }
            }
            catch { failed = true; throw; }
            finally
            {
                // Checkpoints are bounded even when a header is excluded or not yet Approved.
                if (!failed && progress != null && ((sourceIndex + 1) % 64 == 0 || sourceIndex + 1 == ordered.Length))
                    await progress(Capture(), sourceIndex + 1, ordered.Length);
            }
        }
        foreach (var port in node.Outputs)
            if (port.Shape == "SINGLE" && outputs[port.Id].Count > 1) throw new AggregatePreviewException("AGG_SOURCE_CARDINALITY");
        if (windows.Values.Any(w => w.Match == "OVERLAPS_WHOLE_REPORT"))
            issues.Add(new("AGG_OVERLAP_WHOLE_REPORT", "$.nodes[" + node.Id + "]", "Whole reports can contribute to adjacent target periods; no proration is performed."));
        return new(node, schema, node.Outputs.ToDictionary(p => p.Id, Channel),
            linked.Values.ToArray(), eligible.Values.ToArray(), coverage, windows.Values.Distinct().ToArray(), issues,
            listing.MembershipRevision, listing.CurrentUnitIds, !coverage.Any(c => c.State == "UNKNOWN"));

        async Task<AggregatePayload> Read(AggregateSourceHeader header)
        {
            if (reader is IAggregateBatchPreviewReader batch)
            {
                var index = positions[header.Pin.ReportId];
                if (index > preparedThrough)
                {
                    var items = ordered.Skip(index).Take(64).ToArray();
                    await batch.PreparePayloadBatchAsync(context, items, actor, ct);
                    preparedThrough = index + items.Length - 1;
                }
            }
            var members = node.Outputs.Select(p => p.MemberId!).Concat(rules.Values.Select(r => r.SourceDateMemberId).Where(id => id != null).Select(id => id!)).ToHashSet(StringComparer.Ordinal);
            if (members.Any(id => !schema.Members.ContainsKey(id))) throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
            var requestedSchema = schema with { Members = schema.Members.Where(p => members.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value) };
            var value = await reader.ReadPayloadAsync(context, header, requestedSchema, actor, ct);
            if (value.Pin != header.Pin) throw new AggregatePreviewException("AGG_INPUT_STALE");
            foreach (var member in value.Values)
                if(!node.Outputs.Any(p=>p.MemberId==member.Key)||!node.Outputs.Where(p=>p.MemberId==member.Key).All(p=>spooled.Contains(p.Id) || stagedLists.Contains(p.Id)))
                    AggregateValueBudget.Charge(member.Value, budget);
            return value;
        }
    }
}
