using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal static class AggregateOverlay
{
    internal static AggregateRecipeDto Validate(AggregateRecipeDto recipe)
    {
        var parsed = AggregateMappingValidator.Parse(System.Text.Json.JsonSerializer.Serialize(recipe, AggregateCanonical.Json));
        if (!parsed.StructurallyValid) throw new AggregatePreviewException(parsed.Issues[0].Code, parsed.Issues[0].Path);
        return parsed.Recipe!;
    }
    internal static AggregateRecipeDto Effective(AggregateConfigVersion version, AggregateInstanceState instance)
    {
        if (instance.RawDraft != null || instance.State == "NEEDS_REPAIR") throw new AggregatePreviewException("AGG_MAPPING_NEEDS_REPAIR");
        var recipe = Validate(version.Recipe);
        var overlay = instance.Overrides;
        if (overlay != null)
        {
            if (overlay.BaseRecipeHash != version.RecipeHash || overlay.TimeRules == null || overlay.Expressions == null || overlay.Tables == null)
                throw new AggregatePreviewException("AGG_OVERRIDE_BASE_STALE");
            Unique(overlay.TimeRules.Select(r => r.Id));
            Unique(overlay.Expressions.Select(e => e.NodeId + ":" + e.PortId));
            Unique(overlay.Tables.Select(t => t.NodeId));
            foreach (var rule in overlay.TimeRules)
            {
                var index = recipe.TimeRules.FindIndex(r => r.Id == rule.Id);
                if (index < 0) throw new AggregatePreviewException("AGG_OVERRIDE_PATH_MISSING");
                recipe.TimeRules[index] = rule;
            }
            foreach (var replacement in overlay.Expressions)
            {
                var node = recipe.Nodes.SingleOrDefault(n => n.Id == replacement.NodeId && n.Kind == "CALCULATION");
                var index = node?.Expressions?.FindIndex(e => e.PortId == replacement.PortId) ?? -1;
                if (index < 0) throw new AggregatePreviewException("AGG_OVERRIDE_PATH_MISSING");
                node!.Expressions![index] = node.Expressions[index] with { Expression = replacement.Expression };
            }
            foreach (var replacement in overlay.Tables)
            {
                var index = recipe.Nodes.FindIndex(n => n.Id == replacement.NodeId && n.Kind == "CALCULATION" && n.TableAssignments != null);
                if (index < 0) throw new AggregatePreviewException("AGG_OVERRIDE_PATH_MISSING");
                recipe.Nodes[index] = recipe.Nodes[index] with { TableAssignments = replacement.Assignments };
            }
        }
        return Prune(Validate(recipe), instance.UnlinkedMembers);
    }
    internal static (AggregateInstanceOverrideDto? Overlay, List<AggregateMigrationConflictDto> Conflicts) Rebase(
        AggregateInstanceState instance, AggregateConfigVersion oldVersion, AggregateConfigVersion next,
        IReadOnlyList<AggregateMigrationChoiceDto> choices)
    {
        var overlay = instance.Overrides; var conflicts = new List<AggregateMigrationConflictDto>();
        if (overlay == null) return (null, conflicts);
        if (overlay.BaseRecipeHash != oldVersion.RecipeHash) throw new AggregatePreviewException("AGG_OVERRIDE_BASE_STALE");
        var used = new HashSet<AggregateMigrationChoiceDto>();
        bool Keep(string nodeId, string? portId, string? ruleId, object? before, object? after)
        {
            if (AggregateCanonical.Hash(before) == AggregateCanonical.Hash(after)) return true;
            var matching = choices.Where(c => c.InstanceId == instance.Id && c.NodeId == nodeId && c.PortId == portId && c.TimeRuleId == ruleId).ToArray();
            if (matching.Length > 1) throw new AggregatePreviewException("AGG_REBASE_CHOICE_INVALID");
            if (matching.Length == 0)
            { conflicts.Add(new(instance.Id, nodeId, portId, ruleId, after == null ? "AGG_OVERRIDE_PATH_MISSING" : "AGG_OVERRIDE_CONFLICT", after == null ? ["USE_NEW_BASE"] : ["KEEP_OVERRIDE", "USE_NEW_BASE"])); return true; }
            var choice = matching[0]; used.Add(choice);
            if (choice.Resolution == "USE_NEW_BASE") return false;
            if (choice.Resolution != "KEEP_OVERRIDE" || after == null) throw new AggregatePreviewException("AGG_REBASE_CHOICE_INVALID");
            return true;
        }
        var time = overlay.TimeRules.Where(t => Keep("", null, t.Id, oldVersion.Recipe.TimeRules.SingleOrDefault(r => r.Id == t.Id), next.Recipe.TimeRules.SingleOrDefault(r => r.Id == t.Id))).ToList();
        var expressions = overlay.Expressions.Where(e => Keep(e.NodeId, e.PortId, null,
            oldVersion.Recipe.Nodes.SingleOrDefault(n => n.Id == e.NodeId)?.Expressions?.SingleOrDefault(x => x.PortId == e.PortId),
            next.Recipe.Nodes.SingleOrDefault(n => n.Id == e.NodeId)?.Expressions?.SingleOrDefault(x => x.PortId == e.PortId))).ToList();
        var tables = overlay.Tables.Where(t => Keep(t.NodeId, null, null,
            oldVersion.Recipe.Nodes.SingleOrDefault(n => n.Id == t.NodeId)?.TableAssignments,
            next.Recipe.Nodes.SingleOrDefault(n => n.Id == t.NodeId)?.TableAssignments)).ToList();
        if (choices.Any(c => c.InstanceId == instance.Id && !used.Contains(c))) throw new AggregatePreviewException("AGG_REBASE_CHOICE_INVALID");
        return (new(next.RecipeHash, time, expressions, tables), conflicts);
    }
    internal static AggregateRecipeDto Prune(AggregateRecipeDto recipe, IReadOnlyList<string> unlinked)
    {
        if (unlinked.Count == 0) return recipe;
        var target = recipe.Nodes.Single(n => n.Kind == "TARGET");
        if (unlinked.Any(id => !target.Inputs.Any(p => p.MemberId == id))) throw new AggregatePreviewException("AGG_UNLINK_TARGET_INVALID");
        var keptInputs = new HashSet<(string, string)>(); var keptOutputs = new HashSet<(string, string)>();
        void Input(string nodeId, string portId)
        {
            if (!keptInputs.Add((nodeId, portId))) return;
            var edge = recipe.Edges.Single(e => e.To.NodeId == nodeId && e.To.PortId == portId);
            Output(edge.From.NodeId, edge.From.PortId);
        }
        void Output(string nodeId, string portId)
        {
            if (!keptOutputs.Add((nodeId, portId))) return;
            var node = recipe.Nodes.Single(n => n.Id == nodeId);
            if (node.Kind == "SOURCE") return;
            if (node.Kind == "FILTER")
            {
                // All filter channels remain paired; predicates may inspect any of them.
                foreach (var output in node.Outputs) keptOutputs.Add((nodeId, output.Id));
                foreach (var input in node.Inputs) Input(nodeId, input.Id);
            }
            else
            {
                var expression = node.Expressions?.SingleOrDefault(e => e.PortId == portId)?.Expression;
                if (expression != null) foreach (var id in References(expression)) Input(nodeId, id);
                foreach (var assignment in node.TableAssignments?.Where(a => a.TargetOutputPortId == portId) ?? [])
                {
                    if (assignment.SourceInputPortId != null) Input(nodeId, assignment.SourceInputPortId);
                    if (assignment.Expression != null) foreach (var id in References(assignment.Expression)) Input(nodeId, id);
                }
            }
        }
        foreach (var port in target.Inputs.Where(p => !unlinked.Contains(p.MemberId!))) Input(target.Id, port.Id);
        if (keptInputs.Count == 0) throw new AggregatePreviewException("AGG_ALL_TARGETS_UNLINKED");
        var nodes = recipe.Nodes.Select(n => n with
        {
            Inputs = n.Inputs.Where(p => keptInputs.Contains((n.Id, p.Id))).ToList(),
            Outputs = n.Outputs.Where(p => keptOutputs.Contains((n.Id, p.Id))).ToList(),
            Expressions = n.Expressions?.Where(e => keptOutputs.Contains((n.Id, e.PortId))).ToList(),
            TableAssignments = n.TableAssignments?.Where(a => keptOutputs.Contains((n.Id, a.TargetOutputPortId))).ToList()
        }).Where(n => n.Inputs.Count + n.Outputs.Count > 0).ToList();
        var timeIds = nodes.SelectMany(n => n.Outputs).Select(p => p.TimeRuleId).ToHashSet();
        return Validate(recipe with { Nodes = nodes, Edges = recipe.Edges.Where(e => keptInputs.Contains((e.To.NodeId, e.To.PortId))).ToList(),
            TimeRules = recipe.TimeRules.Where(t => timeIds.Contains(t.Id)).ToList() });
    }
    private static IEnumerable<string> References(AggregateExpressionDto expression)
        => (expression.Kind is "INPUT" or "TABLE_RANGE" or "TABLE_FILTER" or "LIST_PIPELINE" ? new[] { expression.Ref! } : [])
            .Concat((expression.Arguments ?? []).SelectMany(References)).Distinct(StringComparer.Ordinal);
    private static void Unique(IEnumerable<string> ids)
    { var values = ids.ToArray(); if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new AggregatePreviewException("AGG_OVERRIDE_DUPLICATE"); }
}
