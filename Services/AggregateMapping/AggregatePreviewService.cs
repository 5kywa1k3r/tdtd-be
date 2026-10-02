using System.Text.Json;
using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

// A read-only preview, deliberately not an Apply token issuer (P03 owns commit fencing).
[System.Text.Json.Serialization.JsonConverter(typeof(AggregatePreviewEnvelopeConverter))]
internal sealed record AggregatePreviewEnvelope(AggregatePreviewResponseDto Preview,
    IReadOnlyDictionary<string, IReadOnlyList<AggregateTrace>> Lineage,
    IReadOnlyList<AggregatePreviewValueDiff> Diff, IReadOnlyList<AggregateFunctionTrace> Functions,
    IReadOnlyList<string> CurrentUnitIds)
{
    // Values actually read before filtering/calculation, with exact numeric evidence. Frozen
    // submissions can explain their old result after a child is returned and edited later.
    public IReadOnlyList<AggregateSourceValueEvidence> SourceValues { get; init; } = [];
    public IReadOnlyList<AggregateListPipelineTrace> ListOperations { get; init; } = [];
}
internal sealed record AggregateSourceValueEvidence(string NodeId, string PortId, AggregateFormPinDto Form,
    JsonElement Value, IReadOnlyList<AggregateTrace> Trace);
internal sealed record AggregatePreviewProgress(string SourceNodeId, int SourceNumber, int SourceCount, int Processed, int Total,
    IReadOnlyList<AggregateTargetResultDto> Results);
internal sealed class AggregatePreviewService(IAggregatePreviewReader reader, IAggregateContentSink? contentSink = null, IAggregateListSink? listSink = null)
{
    internal async Task<AggregatePreviewEnvelope> PreviewAsync(AggregatePreviewRequestDto request, string actor, CancellationToken ct,
        Func<AggregatePreviewProgress, Task>? progress = null)
    {
        var budget = new AggregateBudget(ct, duration: progress == null ? null : TimeSpan.FromMinutes(5));
        var raw = JsonSerializer.Serialize(request.Recipe, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var parsed = AggregateMappingValidator.Parse(raw);
        if (!parsed.StructurallyValid) throw new AggregatePreviewException(parsed.Issues[0].Code, parsed.Issues[0].Path);
        var recipe = parsed.Recipe!;
        var context = await reader.ReadContextAsync(request.Context, actor, ct);
        var contextIssue = AggregatePeriodContextContract.Compare(request.Context, context.Context);
        if (contextIssue != null) throw new AggregatePreviewException(contextIssue.Code);
        if (!AggregateMappingPolicy.Decide(AggregateAction.EditMapping, context.Authority).Allowed)
            throw new AggregatePreviewException("AGG_PREVIEW_FORBIDDEN");
        if (request.Expected.PayloadRevision != context.Revisions.PayloadRevision || request.Expected.LifecycleRevision != context.Revisions.LifecycleRevision
            || request.Expected.InstanceRevision != context.Revisions.InstanceRevision || request.Expected.ConfigRevision != context.Revisions.ConfigRevision
            || request.Expected.TargetSchemaHash != context.TargetSchema.Pin.SchemaHash)
            throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
        if (recipe.Nodes.Single(n => n.Kind == "TARGET").Form != context.TargetSchema.Pin)
            throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
        var sourceNodes = recipe.Nodes.Where(n => n.Kind == "SOURCE").ToArray();
        if (request.Selection?.Sources == null || request.Selection.Sources.Count != sourceNodes.Length
            || request.Selection.Sources.Any(s => s == null) || request.Selection.Sources.Select(s => s.SourceNodeId).Distinct().Count() != sourceNodes.Length
            || request.Selection.Sources.Any(s => !sourceNodes.Any(n => n.Id == s.SourceNodeId)))
            throw new AggregatePreviewException("AGG_SOURCE_SELECTION_INVALID");
        var resolved = new List<AggregateResolvedSource>();
        foreach (var node in sourceNodes)
        {
            var result = await new AggregateSourceResolver(reader, contentSink, listSink).ResolveAsync(node, recipe,
                request.Selection.Sources.Single(s => s.SourceNodeId == node.Id), context, actor, budget, ct,
                progress == null ? null : async (capture, processed, total) => {
                    var interim = await EvaluateAsync(request, recipe, context, [.. resolved, capture], actor,
                        new AggregateBudget(ct), ct, partial: true);
                    await progress(new(node.Id, resolved.Count + 1, sourceNodes.Length, processed, total, interim.Preview.Results));
                });
            resolved.Add(result);
        }
        return await EvaluateAsync(request, recipe, context, resolved, actor, budget, ct);
    }
    private async Task<AggregatePreviewEnvelope> EvaluateAsync(AggregatePreviewRequestDto request, AggregateRecipeDto recipe,
        AggregateReadContext context, IReadOnlyList<AggregateResolvedSource> resolved, string actor, AggregateBudget budget,
        CancellationToken ct, bool partial = false)
    {
        var channels = new Dictionary<(string, string), AggregateChannel>();
        var waiting = new HashSet<(string, string)>();
        foreach (var source in recipe.Nodes.Where(n => n.Kind == "SOURCE"))
            foreach (var port in source.Outputs)
            {
                var capture = resolved.SingleOrDefault(r => r.Node.Id == source.Id);
                var channel = capture?.Outputs[port.Id] ?? new AggregateChannel(port.ValueType, port.Shape, [], port.ValueType == "TABLE");
                channels[(source.Id, port.Id)] = channel;
                if (partial && (channel.Items.Count == 0 || port.Shape == "SINGLE" || capture?.Complete != true)) waiting.Add((source.Id, port.Id));
            }
        var membership = AggregateDigest.Of(resolved.Select(r => new { r.Node.Id, r.MembershipRevision }).OrderBy(r => r.Id));
        var evaluator = new AggregateEvaluator(budget, resolved.SelectMany(r => r.UnitIds).Distinct(StringComparer.Ordinal).ToArray());
        var pending = recipe.Nodes.Where(n => n.Kind != "SOURCE").ToList();
        var targetResults = new List<AggregateTargetResultDto>();
        var lineage = new Dictionary<string, IReadOnlyList<AggregateTrace>>();
        var changes = new List<AggregateChangeDto>();
        var valueDiffs = new List<AggregatePreviewValueDiff>();
        while (pending.Count > 0)
        {
            budget.Spend();
            var node = pending.FirstOrDefault(n => recipe.Edges.Where(e => e.To.NodeId == n.Id).All(e => channels.ContainsKey((e.From.NodeId, e.From.PortId))))
                ?? throw new AggregatePreviewException("AGG_GRAPH_CYCLE");
            var inputs = recipe.Edges.Where(e => e.To.NodeId == node.Id).ToDictionary(e => e.To.PortId, e => channels[(e.From.NodeId, e.From.PortId)]);
            var inputWaiting = partial && recipe.Edges.Where(e => e.To.NodeId == node.Id).Any(e => waiting.Contains((e.From.NodeId, e.From.PortId)));
            if (node.Kind == "FILTER")
            {
                if (inputWaiting)
                {
                    foreach (var port in node.Outputs) { channels[(node.Id, port.Id)] = new(port.ValueType, port.Shape, [], port.ValueType == "TABLE"); waiting.Add((node.Id, port.Id)); }
                }
                else
                foreach (var port in evaluator.Filter(node, inputs)) channels[(node.Id, port.Key)] = port.Value;
            }
            else if (node.Kind == "CALCULATION")
            {
                foreach (var output in node.Outputs)
                {
                    AggregateChannel result;
                    var expression = node.Expressions!.SingleOrDefault(e => e.PortId == output.Id);
                    if (partial && (inputWaiting || expression == null || RequiresComplete(expression.Expression)))
                    {
                        channels[(node.Id, output.Id)] = new(output.ValueType, output.Shape, [], output.ValueType == "TABLE");
                        waiting.Add((node.Id, output.Id)); continue;
                    }
                    if (expression != null)
                    {
                        var type = evaluator.Infer(expression.Expression, inputs.ToDictionary(p => p.Key, p => new AggregateExpressionType(p.Value.Type, p.Value.Shape, p.Value.TableOrigin, p.Value.ListSchema)));
                        if (type.Type != output.ValueType || type.Shape != output.Shape) throw new AggregatePreviewException("AGG_EXPRESSION_OUTPUT_TYPE");
                        if (type.Type == "TABLE" && expression.Expression.Name != "REPORT_TEXT_TABLE" && AggregateTableAdapter.HasScalarInput(expression.Expression, inputs))
                            throw new AggregatePreviewException("AGG_FIELD_TABLE_BRIDGE_DEFERRED");
                        try { result = listSink == null ? evaluator.Evaluate(expression.Expression, inputs)
                            : await evaluator.EvaluateWithListsAsync(expression.Expression, inputs, listSink, context, ct); }
                        catch (AggregatePreviewException ex) when (partial && ex.Code is "AGG_DIVIDE_BY_ZERO" or "AGG_NUMBER_OVERFLOW")
                        {
                            // A partial denominator may still become nonzero. Final evaluation remains authoritative.
                            channels[(node.Id, output.Id)] = new(output.ValueType, output.Shape, [], output.ValueType == "TABLE");
                            waiting.Add((node.Id, output.Id)); continue;
                        }
                    }
                    else
                    {
                        var targetPort = ResolveTarget(node.Id, output.Id);
                        if (!context.TargetSchema.Members.TryGetValue(targetPort.MemberId!, out var member) || member.Table == null)
                            throw new AggregatePreviewException("AGG_TABLE_TARGET_REQUIRED");
                        result = AggregateTableAdapter.Assign(member.Table, output.Id, node.TableAssignments!, inputs, evaluator, budget);
                    }
                    channels[(node.Id, output.Id)] = result;
                }
            }
            else if (node.Kind == "TARGET")
            {
                foreach (var port in node.Inputs)
                {
                    var edge = recipe.Edges.Single(e => e.To.NodeId == node.Id && e.To.PortId == port.Id);
                    if (partial && waiting.Contains((edge.From.NodeId, edge.From.PortId)))
                    { targetResults.Add(new(node.Id, port.Id, port.ValueType, "WAITING", null, null, null, "", null)); continue; }
                    if (!context.TargetSchema.Members.TryGetValue(port.MemberId!, out var member) || member.Type != port.ValueType)
                        throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
                    var channel = inputs[port.Id];
                    if (channel.TableOrigin && port.ValueType != "TABLE") throw new AggregatePreviewException("AGG_FIELD_TABLE_BRIDGE_DEFERRED");
                    var value = AggregateEvaluator.Scalar(channel);
                    if (value.Value.State == "VALUE" && member.Type is "CHOICE_ONE" or "CHOICE_MANY")
                    {
                        var codes = member.Type == "CHOICE_ONE" ? new[] { value.Value.Text! } : value.Value.Choices!;
                        if (member.AllowedChoiceCodes == null || codes.Any(c => !member.AllowedChoiceCodes.Contains(c, StringComparer.Ordinal)))
                            throw new AggregatePreviewException("AGG_TARGET_CHOICE_UNAVAILABLE");
                    }
                    if (value.Value.Table is { } table) ValidateTargetTable(table, member.Table!);
                    if (value.Value.List is { } list)
                    {
                        ValidateTargetList(list, member.List!);
                        if (reader is IAggregateListTargetValidator validator) await validator.ValidateListResultAsync(context, port.MemberId!, list, ct);
                    }
                    var reference = AggregateDigest.Of(new { request.InstanceId, NodeId = node.Id, PortId = port.Id, value.Trace });
                    lineage[reference] = value.Trace; // Keep arithmetic multiplicity; locks are distinct below.
                    var wire = value.Value.Table?.ContentRows != null && contentSink != null
                        ? JsonSerializer.SerializeToElement(await contentSink.CaptureAsync(context, value.Value.Table, ct), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                        : value.Value.List != null && listSink != null
                            ? JsonSerializer.SerializeToElement(await listSink.CaptureAsync(context, value.Value.List, ct), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                            : ToWire(value.Value);
                    var previous = context.PreviousValues.TryGetValue(port.MemberId!, out var old)
                        ? old.State == "INVALID" ? JsonSerializer.SerializeToElement(old.Text)
                            : old.List != null && listSink != null ? JsonSerializer.SerializeToElement(await listSink.CaptureAsync(context, old.List, ct), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                            : ToWire(old) : null;
                    var previousRef = previous == null ? null : AggregateDigest.Of(previous);
                    var nextRef = wire == null ? null : AggregateDigest.Of(wire);
                    valueDiffs.Add(new(node.Id, port.Id, previous, wire, old?.State ?? "MISSING", value.Value.State));
                    changes.Add(new(new(node.Id, port.Id), previousRef == nextRef ? "UNCHANGED" : "CHANGED", previousRef, nextRef));
                    targetResults.Add(new(node.Id, port.Id, port.ValueType, partial ? (value.Value.State == "VALUE" ? "PROVISIONAL" : "WAITING") : value.Value.State == "VALUE" ? "RESULT" : "NO_RESULT", wire,
                        value.Value.Number?.Numerator.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        value.Value.Number?.Denominator.ToString(System.Globalization.CultureInfo.InvariantCulture), reference, previousRef));
                }
            }
            pending.Remove(node);
        }
        var linked = resolved.SelectMany(r => r.Linked).GroupBy(p => p.ReportId).Select(g =>
        {
            if (g.Distinct().Count() != 1) throw new AggregatePreviewException("AGG_INPUT_STALE");
            return g.First();
        }).OrderBy(p => p.ReportId, StringComparer.Ordinal).ToArray();
        if (!await reader.IsCurrentAsync(context, linked, membership, actor, ct)) throw new AggregatePreviewException("AGG_INPUT_STALE");
        var usedIds = lineage.Values.SelectMany(t => t).Select(t => t.ReportId).ToHashSet(StringComparer.Ordinal);
        var contributing = resolved.SelectMany(r => r.Eligible).Where(p => usedIds.Contains(p.ReportId)).Distinct().OrderBy(p => p.ReportId, StringComparer.Ordinal).ToList();
        var coverage = resolved.SelectMany(r => r.Coverage).Distinct().ToList();
        var missing = coverage.Any(c => c.State is "MISSING" or "DRAFT" or "SUBMITTED");
        var complete = resolved.All(r => r.Complete) && !(missing && recipe.Nodes.Any(n => n.Outputs.Any(p => p.ValueType == "LIST")));
        var digest = AggregateDigest.Of(new { actor, context.AuthorizationFingerprint, context.Context, context.Revisions,
            recipe, request.Selection, linked, membership, windows = resolved.SelectMany(r => r.Windows).ToArray(), numeric = "EXACT_RATIONAL_6_TO_EVEN",
            dateFilters = AggregatePartialDate.FilterSemantics });
        if (!complete)
        {
            targetResults = targetResults.Select(r => r with { State = partial ? "WAITING" : "UNAVAILABLE", Value = null, Numerator = null, Denominator = null }).ToList();
            valueDiffs = valueDiffs.Select(d => d with { After = null, AfterState = "UNAVAILABLE" }).ToList();
            changes = changes.Select(d => d with { Kind = "UNRESOLVED", NextResultRef = null }).ToList();
        }
        var issues = resolved.SelectMany(r => r.Issues).ToList();
        foreach (var operation in evaluator.ListTrace.Where(t => t.Omitted > 0))
            issues.Add(new("AGG_LIST_TOP_N_TRUNCATED", "$.listOperations", $"Có {operation.Matched} phần tử phù hợp; đã lấy {operation.Selected}, còn {operation.Omitted}. Có thể tăng N để lấy thêm."));
        if (!complete) issues.Add(new("AGG_COVERAGE_UNKNOWN", "$.coverage", "Coverage cannot be established from declared data windows."));
        AggregateCapabilityDto Capability(AggregateAction action) => AggregateMappingPolicy.Decide(action, context.Authority);
        var capabilities = new AggregateCapabilitiesDto(Capability(AggregateAction.ReadConfig), Capability(AggregateAction.EditConfig),
            Capability(AggregateAction.EditMapping), new(true, null), new(false, "AGG_P03_NOT_OPEN"), Capability(AggregateAction.Submit),
            Capability(AggregateAction.Approve), Capability(AggregateAction.Return), Capability(AggregateAction.ReadLineage));
        var response = new AggregatePreviewResponseDto(Guid.NewGuid().ToString("N"), context.Context, request.InstanceId,
            context.Revisions with { InputDigest = digest }, partial ? "PROCESSING" : complete ? "VALID" : "UNAVAILABLE", partial ? "PROCESSING" : !complete ? "UNKNOWN" : missing ? "MISSING_SOURCES" : "COMPLETE",
            targetResults, resolved.SelectMany(r => r.Windows).Distinct().ToList(), coverage, contributing, linked.ToList(), issues, changes,
            new(linked.Select(p => p.ReportId).ToList(), coverage.Where(c => c.State == "MISSING").Select(c => c.SlotKey).Distinct().ToList(), complete ? "RESOLVED" : "UNKNOWN"),
            capabilities, null, null);
        var sourceEvidence = new List<AggregateSourceValueEvidence>();
        if (complete && !partial) foreach (var source in resolved) foreach (var port in source.Outputs)
        {
            var contentCall = recipe.Edges.Where(e => e.From.NodeId == source.Node.Id && e.From.PortId == port.Key)
                .SelectMany(e => recipe.Nodes.Where(n => n.Id == e.To.NodeId).SelectMany(n => n.Expressions ?? []))
                .Select(e => e.Expression).FirstOrDefault(e => e.Name == "REPORT_TEXT_TABLE");
            if (contentSink != null && port.Value.Type == "TEXT" && contentCall != null)
            {
                var contentTable = AggregateEvaluator.Scalar(AggregateContentTable.Build(port.Value, contentCall.Options!, budget)).Value.Table!;
                var reference = await contentSink.CaptureAsync(context, contentTable, ct);
                sourceEvidence.Add(new(source.Node.Id, port.Key, source.Schema.Pin,
                    JsonSerializer.SerializeToElement(new { type = "TABLE", state = "VALUE", value = reference }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    port.Value.Items.SelectMany(i => i.Trace).ToArray()));
            }
            else if (port.Value.Type == "LIST" && listSink != null)
            {
                foreach (var item in port.Value.Items)
                {
                    var reference = item.Value.ListReference ?? await listSink.CaptureAsync(context,
                        item.Value.List ?? throw new AggregatePreviewException("AGG_LIST_SOURCE_UNAVAILABLE"), ct);
                    sourceEvidence.Add(new(source.Node.Id, port.Key, source.Schema.Pin,
                        JsonSerializer.SerializeToElement(reference, new JsonSerializerOptions(JsonSerializerDefaults.Web)), []));
                }
            }
            else sourceEvidence.AddRange(port.Value.Items.Select(item => new AggregateSourceValueEvidence(source.Node.Id, port.Key, source.Schema.Pin, Evidence(item.Value), item.Trace)));
        }
        if (complete && !partial && listSink != null)
            for (var i = 0; i < evaluator.ListTrace.Count; i++)
                evaluator.ListTrace[i] = evaluator.ListTrace[i] with { Selection = await listSink.CaptureAsync(context, evaluator.ListSelections[i], ct) };
        return new(response, lineage, valueDiffs, evaluator.FunctionTrace, resolved.SelectMany(r => r.UnitIds).Distinct(StringComparer.Ordinal).ToArray())
        {
            SourceValues = sourceEvidence, ListOperations = evaluator.ListTrace
        };

        AggregatePortDto ResolveTarget(string nodeId, string portId)
        {
            var edges = recipe.Edges.Where(e => e.From.NodeId == nodeId && e.From.PortId == portId).ToArray();
            if (edges.Length != 1) throw new AggregatePreviewException("AGG_TABLE_TARGET_AMBIGUOUS");
            var next = recipe.Nodes.Single(n => n.Id == edges[0].To.NodeId);
            if (next.Kind == "TARGET") return next.Inputs.Single(p => p.Id == edges[0].To.PortId);
            if (next.Kind != "FILTER") throw new AggregatePreviewException("AGG_TABLE_TARGET_AMBIGUOUS");
            return ResolveTarget(next.Id, next.Outputs[next.Inputs.FindIndex(p => p.Id == edges[0].To.PortId)].Id);
        }
    }
    // Cardinality, order and table shape need the complete set. Provisional arithmetic
    // uses the same evaluator; these outputs wait instead of fabricating an answer.
    private static bool RequiresComplete(AggregateExpressionDto expression)
        => expression.Kind == "LIST_PIPELINE" || expression.Kind.StartsWith("TABLE", StringComparison.Ordinal)
            || expression.Kind == "CALL" && expression.Name is not ("SUM" or "COUNT" or "AVG" or "MIN" or "MAX" or "CONCAT")
            || (expression.Arguments?.Any(RequiresComplete) ?? false);
    internal static JsonElement? ToWire(AggregateValue value)
    {
        if (value.State != "VALUE") return null;
        if (value.StoredWire.HasValue) return value.StoredWire;
        if (value.ContentReference != null) return JsonSerializer.SerializeToElement(value.ContentReference, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (value.ListReference != null) return JsonSerializer.SerializeToElement(value.ListReference, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        object? wire = value.Type switch
        {
            "NUMBER" => value.Number!.ToWire(), "BOOLEAN" => value.Boolean, "CHOICE_MANY" => value.Choices,
            "LIST" => AggregateListWire.Encode(value.List!),
            "TABLE" => new { columns = value.Table!.Schema.Columns, rows = value.Table.Rows.Select((row, r) =>
                new { unitId = value.Table.Units[r], note = value.Table.ContentRows?[r], cells = row.Select(cell => new { type = cell.Value.Type, state = cell.Value.State, value = ToWire(cell.Value), lineage = cell.Trace }) }) },
            _ => value.Text
        };
        return JsonSerializer.SerializeToElement(wire, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    private static JsonElement Evidence(AggregateValue value) => JsonSerializer.SerializeToElement(new
    {
        value.Type, value.State, Value = value.Table == null ? ToWire(value) : JsonSerializer.SerializeToElement(new {
            columns = value.Table.Schema.Columns,
            rows = value.Table.Rows.Select((row, r) => new { unitId = value.Table.Units[r], note = value.Table.ContentRows?[r],
                cells = row.Select(cell => new { type = cell.Value.Type, state = cell.Value.State, value = ToWire(cell.Value),
                    numerator = cell.Value.Number?.Numerator.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    denominator = cell.Value.Number?.Denominator.ToString(System.Globalization.CultureInfo.InvariantCulture), lineage = cell.Trace }) })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        Numerator = value.Number?.Numerator.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Denominator = value.Number?.Denominator.ToString(System.Globalization.CultureInfo.InvariantCulture)
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static void ValidateTargetTable(AggregateTable source, AggregateTableSchema target)
    {
        if (source.ContentRows != null && (target.Layout != "vertical" || target.Columns.Count != 2
            || target.CellTypes.Count != 1 || target.CellTypes[0].Any(t => t != "TEXT")))
            throw new AggregatePreviewException("AGG_CONTENT_TABLE_TARGET");
        if (source.Schema.Columns.Count != target.Columns.Count || (target.Layout == "matrix" && source.Rows.Count != target.Rows.Count))
            throw new AggregatePreviewException("AGG_TABLE_SHAPE");
        for (var r = 0; r < source.Rows.Count; r++) for (var c = 0; c < target.Columns.Count; c++)
            if (source.Rows[r][c].Value.Type != target.CellTypes[target.Layout == "matrix" ? r : 0][c]) throw new AggregatePreviewException("AGG_TABLE_CELL_TYPE");
    }
    private static void ValidateTargetList(AggregateListValue source, AggregateListSchema target)
    {
        if (target == null || source.Schema.Fields.Count != target.Fields.Count
            || source.Schema.Fields.Any(f => !target.Fields.Any(t => t.Id == f.Id && t.Type == f.Type)) || source.Records.Count > 200)
            throw new AggregatePreviewException("AGG_LIST_TARGET_SCHEMA");
    }
}
