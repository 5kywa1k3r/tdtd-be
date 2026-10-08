using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

// Syntax/graph contract only: successful validation does NOT authorize evaluation or Apply.
// Schema member lookup, expression type inference and data-window resolution belong to P02.
internal sealed record AggregateContractValidation(
    AggregateRecipeDto? Recipe, IReadOnlyList<AggregateIssueDto> Issues)
{
    internal bool StructurallyValid => Recipe != null && Issues.Count == 0;
}

internal static class AggregateMappingValidator
{
    internal const int MaxBytes = 262144;
    internal const int MaxNodes = 128;
    internal const int MaxEdges = 512;
    internal const int MaxPortsPerNode = 128;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 48
    };
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
        { "NUMBER", "TEXT", "BOOLEAN", "DATE_ONLY", "DATE_PARTIAL", "CHOICE_ONE", "CHOICE_MANY", "INSTANT", "TABLE", "LIST" };
    private static readonly HashSet<string> Calls = new(StringComparer.Ordinal)
        { "SUM", "AVG", "MIN", "MAX", "COUNT", "COUNT_DISTINCT", "CONCAT", "REPORT_TEXT_TABLE", "APPEND_TABLE", "IF", "ONLY", "IS_BLANK", "IS_PRESENT", "HAS_CHOICE", "TEXT_CONTAINS", "TEXT_STARTS", "TEXT_ENDS", "TEXT_EQUALS" };
    private static readonly HashSet<string> Binary = new(StringComparer.Ordinal)
        { "+", "-", "*", "/", "=", "<>", "<", "<=", ">", ">=", "AND", "OR" };

    internal static AggregateContractValidation Parse(string? raw, string? formulaProbeNode = null)
    {
        var issues = new List<AggregateIssueDto>();
        void Add(string code, string path, string reason) => issues.Add(new(code, path, reason));
        if (string.IsNullOrWhiteSpace(raw) || Encoding.UTF8.GetByteCount(raw) > MaxBytes)
        {
            Add("AGG_RECIPE_SIZE", "$", "Recipe must contain 1..262144 UTF-8 bytes.");
            return new(null, issues);
        }
        AggregateRecipeDto? recipe;
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 48 });
            CheckDuplicates(document.RootElement, "$", Add);
            if (issues.Count > 0) return new(null, issues);
            recipe = JsonSerializer.Deserialize<AggregateRecipeDto>(raw, JsonOptions);
        }
        catch (JsonException)
        {
            // Do not echo raw payloads/values in errors or logs.
            Add("AGG_RECIPE_JSON", "$", "Invalid JSON, unknown property, missing required property or depth exceeded.");
            return new(null, issues);
        }
        if (recipe == null || recipe.Nodes == null || recipe.Edges == null || recipe.TimeRules == null)
        {
            Add("AGG_RECIPE_REQUIRED", "$", "Recipe and collections must be non-null.");
            return new(recipe, issues);
        }
        var extended = recipe.SchemaVersion == 3 && recipe.SemanticProfile == "REPORT_MAPPING_EXTENDED_V1";
        var listProfile = extended || recipe.SchemaVersion == 2 && recipe.SemanticProfile == "REPORT_MAPPING_LIST_V1";
        if (!listProfile && (recipe.SchemaVersion != 1 || recipe.SemanticProfile != "REPORT_MAPPING_V1"))
            Add("AGG_RECIPE_VERSION", "$", "Unsupported schemaVersion or semanticProfile; preserve raw draft.");
        if (!extended && recipe.Nodes.Any(n => n != null && ((n.Expressions?.Any(e => HasExtended(e?.Expression)) ?? false)
            || HasExtended(n.Predicate) || (n.TableAssignments?.Any(a => HasExtended(a.Expression)) ?? false))))
            Add("AGG_EXTENDED_PROFILE_REQUIRED", "$", "New operators require recipe 3 / REPORT_MAPPING_EXTENDED_V1.");
        if (!listProfile && recipe.Nodes.Any(n => n != null &&
            ((n.Inputs?.Concat(n.Outputs ?? []).Any(p => p?.ValueType == "LIST") ?? false)
                || (n.Expressions?.Any(e => HasList(e?.Expression)) ?? false) || HasList(n.Predicate))))
            Add("AGG_LIST_PROFILE_REQUIRED", "$", "List operators require the explicit version 2 List profile.");
        if (recipe.Nodes.Count is < 2 or > MaxNodes || recipe.Edges.Count > MaxEdges || recipe.TimeRules.Count > MaxPortsPerNode)
        {
            Add("AGG_GRAPH_BUDGET", "$", "Graph exceeds the contract budget or has fewer than two nodes.");
            return new(recipe, issues);
        }
        var windows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in recipe.TimeRules)
        {
            if (rule == null || !Identifier(rule.Id) || !windows.Add(rule.Id))
            { Add("AGG_TIME_RULE_ID", "$.timeRules", "Missing or duplicate time-rule ID."); continue; }
            ValidateTimeRule(rule, Add);
        }
        var nodes = new Dictionary<string, AggregateNodeDto>(StringComparer.Ordinal);
        foreach (var node in recipe.Nodes)
        {
            if (node == null || !Identifier(node.Id) || !nodes.TryAdd(node.Id, node))
            { Add("AGG_NODE_ID", "$.nodes", "Missing or duplicate node ID."); continue; }
            var path = "$.nodes[" + node.Id + "]";
            if (node.Inputs == null || node.Outputs == null || node.Inputs.Count + node.Outputs.Count > MaxPortsPerNode)
            { Add("AGG_PORTS_REQUIRED", path, "Non-null bounded port collections are required."); continue; }
            var portIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in node.Inputs.Concat(node.Outputs))
            {
                if (port == null || !Identifier(port.Id) || !portIds.Add(port.Id))
                { Add("AGG_PORT_ID", path, "Port IDs must be unique within the node."); continue; }
                if (!Types.Contains(port.ValueType ?? "") || port.Shape is not ("SINGLE" or "SET"))
                    Add("AGG_PORT_TYPE", path, "Unsupported value type or shape.");
                if (port.TimeRuleId != null && (!windows.Contains(port.TimeRuleId) || node.Kind != "SOURCE"))
                    Add("AGG_TIME_RULE_REF", path, "Only a source port may reference an existing time rule.");
            }
            if (node.Kind is "SOURCE" or "TARGET")
            {
                if (node.Form == null || !Identifier(node.Form.FormId) || !Identifier(node.Form.FamilyId)
                    || node.Form.VersionNo < 1 || !Identifier(node.Form.SchemaHash))
                    Add("AGG_FORM_PIN", path, "A complete immutable form pin is required.");
                if (node.Expressions != null || node.Predicate != null || node.TableAssignments != null)
                    Add("AGG_NODE_FIELDS", path, "Form nodes cannot carry calculation/filter fields.");
                foreach (var port in node.Inputs.Concat(node.Outputs).Where(p => p != null))
                    if (!Identifier(port.MemberId)) Add("AGG_MEMBER_ID", path, "Form ports require stable member IDs.");
                if (node.Kind == "SOURCE")
                {
                    if (node.Inputs.Count != 0 || node.Outputs.Count == 0 || node.Origin is not ("DIRECT_CHILD_REPORTS" or "INTERMEDIATE")
                        || node.SourceCardinality is not ("SINGLE" or "SET"))
                        Add("AGG_SOURCE_SCHEMA", path, "Invalid source direction, origin or cardinality.");
                    if (node.Outputs.Any(p => p != null && p.Shape != node.SourceCardinality))
                        Add("AGG_SOURCE_CARDINALITY", path, "Each source port must carry the declared source cardinality.");
                    if (node.Origin == "DIRECT_CHILD_REPORTS" && node.Outputs.Any(p => p != null && p.TimeRuleId == null))
                        Add("AGG_TIME_RULE_REQUIRED", path, "Choose a data window for each report source port; no implicit all-time scope.");
                    var sourceRuleIds = node.Outputs.Where(p => p != null).Select(p => p.TimeRuleId).Distinct().ToArray();
                    if (recipe.TimeRules.Any(r => r != null && sourceRuleIds.Contains(r.Id) && r.Mode == AggregateReportSetFilter.Mode)
                        && sourceRuleIds.Length != 1)
                        Add("AGG_REPORT_SET_INVALID", path, "All fields of a report-set source must share one metadata filter.");
                }
                else if (node.Outputs.Count != 0 || (formulaProbeNode == null ? node.Inputs.Count == 0 : node.Inputs.Count != 0) || node.Origin != null || node.SourceCardinality != null
                    || node.Inputs.Any(p => p != null && p.Shape != "SINGLE"))
                    Add("AGG_TARGET_SCHEMA", path, "Target ports must be SINGLE inputs with no source metadata.");
            }
            else if (node.Kind is "FILTER" or "CALCULATION")
            {
                if (node.Form != null || node.Origin != null || node.SourceCardinality != null
                    || node.Inputs.Count == 0 || node.Outputs.Count == 0)
                    Add("AGG_NODE_FIELDS", path, "Processing nodes require input/output ports and no form/source metadata.");
                var inputs = node.Inputs.Where(p => p != null).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
                if (node.Kind == "FILTER")
                {
                    if (node.Expressions != null || node.TableAssignments != null || node.Predicate == null)
                        Add("AGG_FILTER_SCHEMA", path, "Filter requires a predicate and no calculation fields.");
                    else ValidateExpression(node.Predicate, inputs, path + ".predicate", 0, Add);
                    // Explicit one-to-one channel pairing by array position; no implicit reducer.
                    if (node.Inputs.Count != node.Outputs.Count || node.Inputs.Where((p, i) =>
                        i < node.Outputs.Count && (p == null || node.Outputs[i] == null || p.ValueType != node.Outputs[i].ValueType || p.Shape != node.Outputs[i].Shape)).Any())
                        Add("AGG_FILTER_CHANNELS", path, "Filter channels must preserve type, shape and array position.");
                }
                else
                {
                    if (node.Predicate != null || node.Expressions == null)
                        Add("AGG_CALCULATION_SCHEMA", path, "Calculation requires expressions and no predicate.");
                    var written = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var expression in node.Expressions ?? [])
                    {
                        if (expression == null || !written.Add(expression.PortId ?? "") || !node.Outputs.Any(p => p?.Id == expression.PortId))
                        { Add("AGG_OUTPUT_EXPRESSION", path, "Each expression must address one unique output."); continue; }
                        ValidateExpression(expression.Expression, inputs, path + ".expressions[" + expression.PortId + "]", 0, Add);
                    }
                    var assignmentIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var assignment in node.TableAssignments ?? [])
                    {
                        if (assignment == null || !Identifier(assignment.Id) || !assignmentIds.Add(assignment.Id))
                        { Add("AGG_TABLE_ASSIGNMENT_ID", path, "Missing/duplicate table assignment ID."); continue; }
                        if (!node.Outputs.Any(p => p != null && p.Id == assignment.TargetOutputPortId && p.ValueType == "TABLE" && p.Shape == "SINGLE")
                            || written.Contains(assignment.TargetOutputPortId))
                            Add("AGG_TABLE_PORT", path, "Coordinate mappings require SINGLE TABLE ports and no expression writer on the same output.");
                        if (assignment.Expression == null)
                        {
                            if (!node.Inputs.Any(p => p != null && p.Id == assignment.SourceInputPortId && p.ValueType == "TABLE" && p.Shape == "SINGLE"))
                                Add("AGG_TABLE_PORT", path, "Copy requires a SINGLE TABLE input.");
                            ValidateArea(assignment.Source, path, Add);
                        }
                        else
                        {
                            if (assignment.SourceInputPortId != null || assignment.Source != null)
                                Add("AGG_TABLE_WRITER", path, "Choose copy or expression, never both.");
                            ValidateExpression(assignment.Expression, inputs, path + ".tableAssignments[" + assignment.Id + "]", 0, Add);
                        }
                        ValidateArea(assignment.Target, path, Add);
                    }
                    foreach (var port in node.Outputs.Where(p => p != null))
                        if (!written.Contains(port.Id) && !(node.TableAssignments?.Any(a => a?.TargetOutputPortId == port.Id) ?? false))
                            Add("AGG_OUTPUT_MISSING", path, "Every output needs an expression or ordered table assignments.");
                }
            }
            else Add("AGG_NODE_KIND", path, "Unsupported node kind; preserve raw draft.");
        }
        // Stop before graph access when malformed/null nodes or ports were found.
        if (issues.Count > 0) return new(recipe, issues);
        if (nodes.Values.Count(n => n.Kind == "TARGET") != 1 || !nodes.Values.Any(n => n.Kind == "SOURCE"))
            Add("AGG_GRAPH_ENDPOINTS", "$.nodes", "Exactly one target and at least one source are required.");
        if (formulaProbeNode != null && (!nodes.TryGetValue(formulaProbeNode, out var probe) || probe.Kind != "CALCULATION"
            || probe.TableAssignments?.Count > 0 || recipe.Edges.Any(e => e?.From?.NodeId == formulaProbeNode)))
            Add("AGG_FORMULA_PROBE_INVALID", "$.nodes", "A read-only formula probe must terminate at its calculation node.");
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var writers = new HashSet<(string, string)>();
        var graph = nodes.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var indegree = nodes.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var edge in recipe.Edges)
        {
            if (edge == null || !Identifier(edge.Id) || !edgeIds.Add(edge.Id) || edge.From == null || edge.To == null
                || !Identifier(edge.From.NodeId) || !Identifier(edge.To.NodeId)
                || !nodes.TryGetValue(edge.From.NodeId, out var from) || !nodes.TryGetValue(edge.To.NodeId, out var to))
            { Add("AGG_EDGE_REF", "$.edges", "Invalid edge ID or endpoint."); continue; }
            var output = from.Outputs.FirstOrDefault(p => p.Id == edge.From.PortId);
            var input = to.Inputs.FirstOrDefault(p => p.Id == edge.To.PortId);
            if (output == null || input == null)
            { Add("AGG_EDGE_DIRECTION", "$.edges", "Edges must connect an existing output to an input."); continue; }
            if (output.ValueType != input.ValueType || output.Shape != input.Shape)
                Add("AGG_EDGE_TYPE", "$.edges", "No implicit type/shape conversion or Field/Table bridge.");
            if (!writers.Add((to.Id, input.Id))) Add("AGG_MULTIPLE_WRITERS", "$.edges", "Input already has a writer.");
            graph[from.Id].Add(to.Id); indegree[to.Id]++;
        }
        foreach (var node in nodes.Values)
            if (node.Inputs.Any(p => !writers.Contains((node.Id, p.Id))))
                Add("AGG_INPUT_UNBOUND", "$.nodes[" + node.Id + "]", "All declared inputs must be connected.");
        var queue = new Queue<string>(indegree.Where(p => p.Value == 0).Select(p => p.Key));
        var seen = 0;
        var phases = nodes.Keys.ToDictionary(id => id, _ => new HashSet<(int Calculations, int Before, int After)>());
        foreach (var root in queue) phases[root].Add((0, 0, 0));
        while (queue.TryDequeue(out var id))
        {
            seen++;
            var nextPhases = phases[id].Select(p => nodes[id].Kind == "CALCULATION" ? (p.Calculations + 1, p.Before, p.After)
                : nodes[id].Kind == "FILTER" ? (p.Calculations, p.Before + (p.Calculations == 0 ? 1 : 0), p.After + (p.Calculations > 0 ? 1 : 0))
                : p).ToHashSet();
            if (nextPhases.Any(p => p.Item1 > 1 || p.Item2 > 1 || p.Item3 > 1))
                Add("AGG_PATH_LIMIT", "$.nodes[" + id + "]", "One calculation and at most one filter on each side per path.");
            foreach (var next in graph[id])
            {
                phases[next].UnionWith(nextPhases.Where(p => p.Item1 <= 1 && p.Item2 <= 1 && p.Item3 <= 1));
                if (--indegree[next] == 0) queue.Enqueue(next);
            }
        }
        if (seen != nodes.Count) Add("AGG_GRAPH_CYCLE", "$.edges", "Graph must be acyclic.");
        return new(recipe, issues);
    }

    private static void ValidateExpression(AggregateExpressionDto? expression, HashSet<string> inputs,
        string path, int depth, Action<string, string, string> add)
    {
        if (expression == null || depth > 16)
        { add("AGG_EXPRESSION_DEPTH", path, "Expression is null or too deeply nested."); return; }
        var e = expression;
        if (e.Kind == "LIST_MERGE")
        {
            if (e.ListMerge == null || e.ListPipeline != null || e.Ref != null || e.Value != null || e.Name != null || e.Arguments != null
                || e.Options != null || e.Area != null || e.ColumnIndex != null || e.Predicate != null)
                add("AGG_LIST_MERGE_SCHEMA", path, "Merge requires its explicit local List inputs and projections.");
            else try { AggregateListMerge.Validate(e.ListMerge, inputs); }
                catch (AggregatePreviewException ex) { add(ex.Code, path, "Invalid List merge."); }
            return;
        }
        if (e.ListMerge != null && e.Kind != "LIST_MERGE") add("AGG_EXPRESSION_SCHEMA", path, "Merge plan belongs only to LIST_MERGE.");
        if (e.Kind == "LIST_PIPELINE")
        {
            if (e.ListPipeline == null || e.Ref == null || !inputs.Contains(e.Ref) || e.Value != null || e.Name != null
                || e.Arguments != null || e.Options != null || e.Area != null || e.ColumnIndex != null || e.Predicate != null)
                add("AGG_LIST_PIPELINE_SCHEMA", path, "A List pipeline requires one local List input and its explicit plan.");
            else try { AggregateListPipeline.Validate(e.ListPipeline); }
                catch (AggregatePreviewException ex) { add(ex.Code, path, "Invalid List pipeline."); }
            return;
        }
        if (e.ListPipeline != null) add("AGG_EXPRESSION_SCHEMA", path, "List plan belongs only to LIST_PIPELINE.");
        if (e.Kind == "TABLE_FILTER")
        {
            if (e.Value != null || e.Name != null || e.Arguments != null || e.Options != null || e.Ref == null || !inputs.Contains(e.Ref) || e.ColumnIndex is not > 0)
                add("AGG_TABLE_FILTER_SCHEMA", path, "Table filter requires an input, value area and positive column coordinate.");
            ValidateArea(e.Area, path, add);
            ValidateExpression(e.Predicate, new HashSet<string>(StringComparer.Ordinal) { "cell" }, path + ".predicate", depth + 1, add);
            return;
        }
        if (e.ColumnIndex != null || e.Predicate != null)
            add("AGG_EXPRESSION_SCHEMA", path, "Cell predicates belong only to TABLE_FILTER.");
        if (e.Kind is "NUMBER" or "TEXT" or "BOOLEAN" or "DATE_ONLY" or "DATE_PARTIAL" or "INSTANT")
        {
            var valid = e.Value != null && e.Name == null && e.Ref == null && e.Arguments == null && e.Options == null && e.Area == null;
            if (e.Kind == "NUMBER") valid &= DecimalLiteral(e.Value);
            if (e.Kind == "BOOLEAN") valid &= e.Value is "true" or "false";
            if (e.Kind == "DATE_ONLY") valid &= Date(e.Value);
            if (e.Kind == "DATE_PARTIAL") valid &= PartialDate(e.Value);
            if (e.Kind == "INSTANT") valid &= DateTimeOffset.TryParseExact(e.Value,
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);
            if (!valid) add("AGG_LITERAL", path, "Invalid typed literal or extraneous expression fields.");
            return;
        }
        if (e.Kind == "TABLE_RANGE")
        {
            if (e.Value != null || e.Name != null || e.Arguments != null || e.Options != null || e.Ref == null || !inputs.Contains(e.Ref))
                add("AGG_EXPRESSION_REF", path, "TABLE_RANGE requires a local table input and a coordinate area.");
            ValidateArea(e.Area, path, add);
            return;
        }
        if (e.Kind == "INPUT" || e.Kind == "GET")
        {
            if (e.Value != null || e.Arguments != null || e.Options != null || e.Area != null || (e.Kind == "INPUT"
                ? e.Name != null || e.Ref == null || !inputs.Contains(e.Ref)
                : e.Ref != null || e.Name != "CURRENT_UNIT_COUNT"))
                add("AGG_EXPRESSION_REF", path, "Unknown input or GET capability.");
            return;
        }
        if (e.Value != null || e.Ref != null || e.Area != null || e.Arguments == null || e.Arguments.Count > 128
            || (e.Kind == "BINARY" ? !Binary.Contains(e.Name ?? "") || e.Arguments.Count != 2 || e.Options != null
                : e.Kind != "CALL" || !(Calls.Contains(e.Name ?? "") || AggregateExtendedFunctions.Names.Contains(e.Name ?? "")) || e.Arguments.Count != (e.Name == "IF" ? 3 : e.Name is "HAS_CHOICE" or "TEXT_CONTAINS" or "TEXT_STARTS" or "TEXT_ENDS" or "TEXT_EQUALS" or "COUNT_CHOICE" ? 2 : 1)))
        { add("AGG_EXPRESSION_SCHEMA", path, "Unknown expression/function or invalid arity."); return; }
        if (e.Name == "COUNT" && e.Options?.Basis is not ("VALUES" or "REPORTS" or "UNITS" or "ROWS" or "PRESENT_ROWS"))
            add("AGG_COUNT_BASIS", path, "COUNT requires explicit basis.");
        if (e.Name == "REPORT_COUNT" && e.Options?.Basis is not ("ELIGIBLE_SOURCES" or "SELECTED_ELEMENTS"))
            add("AGG_COUNT_BASIS", path, "REPORT_COUNT requires an explicit source or selected-element basis.");
        if (e.Name == "COUNT_DISTINCT" && (e.Options?.Trim == null || e.Options.CaseSensitive == null))
            add("AGG_DISTINCT_OPTIONS", path, "DISTINCT requires explicit text normalization settings.");
        if (e.Name == "CONCAT" && (e.Options?.Separator == null || e.Options.Trim == null || e.Options.Order is not ("UNIT_THEN_PERIOD" or "PERIOD_THEN_UNIT")))
            add("AGG_CONCAT_OPTIONS", path, "CONCAT requires explicit separator and deterministic order.");
        if (e.Name == "REPORT_TEXT_TABLE" && e.Options?.Order is not ("UNIT_THEN_PERIOD" or "PERIOD_THEN_UNIT"))
            add("AGG_CONTENT_TABLE_ORDER", path, "Content table requires an explicit deterministic order.");
        if (e.Name == "COUNT_CHOICE" && (e.Arguments[0].Kind != "INPUT" || e.Arguments[1].Kind != "TEXT"
            || !Identifier(e.Arguments[1].Value)))
            add("AGG_CHOICE_COUNT_CODE", path, "Count a choice set by one non-empty stored code, not its display label.");
        if (e.Name == "CONCAT_UNIT" && e.Arguments[0].Kind != "INPUT")
            add("AGG_CONCAT_UNIT_SOURCE", path, "Use a directly connected text set so each opinion retains its reporting unit.");
        if (e.Name == "CHOICE_COUNT_TABLE" && e.Arguments[0].Kind != "INPUT")
            add("AGG_CHOICE_TABLE_SOURCE", path, "Use one directly connected choice set with its option snapshot.");
        if (e.Options?.Basis == "PRESENT_ROWS" && e.Options.ColumnIndex is not > 0)
            add("AGG_COUNT_COLUMN", path, "PRESENT_ROWS requires a positive column coordinate.");
        if (e.Options?.UnitDisplay != null && (e.Name != "REPORT_TEXT_TABLE" || e.Options.UnitDisplay is not ("FULL_NAME" or "SHORT_NAME" or "SYMBOL" or "NONE")))
            add("AGG_CONTENT_UNIT_DISPLAY", path, "Choose the reporting-unit display.");
        if (e.Options != null && ((e.Options.Basis != null && e.Name is not ("COUNT" or "REPORT_COUNT"))
            || (e.Options.Trim != null && e.Name is not ("COUNT_DISTINCT" or "CONCAT"))
            || (e.Options.CaseSensitive != null && e.Name != "COUNT_DISTINCT")
            || (e.Options.Separator != null && e.Name != "CONCAT")
            || (e.Options.Order != null && e.Name is not ("CONCAT" or "REPORT_TEXT_TABLE"))
            || (e.Options.ColumnIndex != null && !(e.Name == "COUNT" && e.Options.Basis == "PRESENT_ROWS"))))
            add("AGG_FUNCTION_OPTIONS", path, "Options do not belong to this function.");
        for (var i = 0; i < e.Arguments.Count; i++) ValidateExpression(e.Arguments[i], inputs, path + ".arguments[" + i + "]", depth + 1, add);
    }

    private static void ValidateTimeRule(AggregateTimeRuleDto rule, Action<string, string, string> add)
    {
        var path = "$.timeRules[" + rule.Id + "]";
        if (rule.Mode == AggregateReportSetFilter.Mode)
        {
            AggregateReportSetFilter.Validate(rule.ReportSet, path, add);
            if (rule.SourceDateBasis != "REPORT_METADATA" || rule.Match != "REPORTS" || rule.StartDate != null
                || rule.EndDate != null || rule.SourceDateMemberId != null || rule.ReportFilter != null)
                add("AGG_REPORT_SET_INVALID", path, "Report-set filters do not imply a declared data window.");
            return;
        }
        if (rule.ReportSet != null) add("AGG_REPORT_SET_INVALID", path, "Report-set conditions require REPORT_SET mode.");
            if (rule.ReportFilter is { } metadata)
            {

                foreach (var (start, end) in new[] { (metadata.FromDate, metadata.ToDate), (metadata.DueFromDate, metadata.DueToDate) })
                    if (start != null && !Date(start) || end != null && !Date(end) || start != null && end != null && string.CompareOrdinal(start, end) > 0)
                        add("AGG_REPORT_FILTER_DATE", path, "Each supplied date bound must be valid; start must not follow end.");
                foreach (var values in new[] { metadata.PeriodKeys, metadata.UnitIds, metadata.Kinds })
                    if (values != null && (values.Count is < 1 or > 1000 || values.Any(v => !Identifier(v)) || values.Distinct(StringComparer.Ordinal).Count() != values.Count))
                        add("AGG_REPORT_FILTER_VALUES", path, "Filter values must be non-empty, unique and bounded.");
                if (metadata.Kinds?.Any(k => k is not ("CURRENT" or "HISTORICAL")) == true)
                    add("AGG_REPORT_FILTER_KIND", path, "Choose current or historical report data.");
            }

        if (rule.SourceDateBasis is not ("DECLARED_DATA_WINDOW" or "DATE_FIELD" or "INSTANT_FIELD")
            || rule.Match is not ("CONTAINED" or "OVERLAPS_WHOLE_REPORT")
            || (rule.SourceDateBasis == "DECLARED_DATA_WINDOW" ? rule.SourceDateMemberId != null : !Identifier(rule.SourceDateMemberId)))
            add("AGG_TIME_BASIS", path, "Explicit source date basis and matching rule are required.");
        var valid = rule.Mode switch
        {
            "EXPLICIT_RANGE" => Date(rule.StartDate) && Date(rule.EndDate) && string.CompareOrdinal(rule.StartDate, rule.EndDate) <= 0,
            "TARGET_DATA_WINDOW" => rule.StartDate == null && rule.EndDate == null,
            "CUMULATIVE_FROM" => Date(rule.StartDate) && rule.EndDate == null,
            _ => false
        };
        if (!valid) add("AGG_TIME_RULE_SCHEMA", path, "Invalid time rule; choose an explicit approved mode.");
    }

    private static void ValidateArea(AggregateTableAreaDto? area, string path, Action<string, string, string> add)
    {
        if (area == null || !(area.Kind switch
            {
                "ALL" => area.RowStart == null && area.RowEnd == null && area.ColumnStart == null && area.ColumnEnd == null,
                "CELL" => area.RowStart is > 0 && area.RowStart == area.RowEnd && area.ColumnStart is > 0 && area.ColumnStart == area.ColumnEnd,
                "ROW" => area.RowStart is > 0 && area.RowStart == area.RowEnd && area.ColumnStart == null && area.ColumnEnd == null,
                "COLUMN" => area.ColumnStart is > 0 && area.ColumnStart == area.ColumnEnd && area.RowStart == null && area.RowEnd == null,
                "RANGE" => area.RowStart is > 0 && area.RowEnd >= area.RowStart && area.ColumnStart is > 0 && area.ColumnEnd >= area.ColumnStart,
                _ => false
            })) add("AGG_TABLE_AREA", path, "Invalid 1-based table coordinate area.");
    }

    private static void CheckDuplicates(JsonElement element, string path, Action<string, string, string> add)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) add("AGG_JSON_DUPLICATE", path, "Duplicate JSON property.");
                CheckDuplicates(property.Value, path + "." + property.Name, add);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicates(item, path + "[]", add);
    }

    private static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
    private static bool HasList(AggregateExpressionDto? e) => e != null && (e.Kind is "LIST_PIPELINE" or "LIST_MERGE"
        || e.ListPipeline != null || (e.Arguments?.Any(HasList) ?? false) || HasList(e.Predicate));
    private static bool HasExtended(AggregateExpressionDto? e) => e != null && (AggregateExtendedFunctions.Names.Contains(e.Name ?? "")
        || e.ListMerge != null || e.ListPipeline?.Version == 2 || (e.Arguments?.Any(HasExtended) ?? false) || HasExtended(e.Predicate));
    private static bool PartialDate(string? value)
    {
        if (DateOnly.TryParseExact(value, ["dd/MM/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return true;
        if (value is { Length: 7 } && value[2] == '/' && value.Where((_, i) => i != 2).All(c => c is >= '0' and <= '9') && int.TryParse(value[..2], out var month)
            && int.TryParse(value[3..], out var year) && month is >= 1 and <= 12 && year is >= 1 and <= 9999) return true;
        return value is { Length: 4 } && value.All(c => c is >= '0' and <= '9') && int.TryParse(value, out var y) && y is >= 1 and <= 9999;
    }
    private static bool Date(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    private static bool DecimalLiteral(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
        var body = value[0] == '-' ? value[1..] : value;
        var parts = body.Split('.');
        return parts.Length is 1 or 2 && parts.All(p => p.Length > 0 && p.All(c => c is >= '0' and <= '9'))
            && (parts[0].Length == 1 || parts[0][0] != '0');
    }
}

// Receives a context already loaded under server ACL, not a second client-supplied DTO.
// Pure identity comparison: never opens/materializes a period or guesses a data window.
internal static class AggregatePeriodContextContract
{
    // Inputs are explicitly declared data bounds read under server ACL. They must never
    // be populated from PeriodKey/PeriodStart/PeriodEnd/DueAtUtc merely by field name.
    internal static (AggregateResolvedWindowDto? Window, AggregateIssueDto? Issue) ResolveWindow(
        AggregateTimeRuleDto rule, string? declaredStart, string? declaredEnd, string authorityRevision)
    {
        static bool Date(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        var start = rule.Mode switch { "EXPLICIT_RANGE" or "CUMULATIVE_FROM" => rule.StartDate, "TARGET_DATA_WINDOW" => declaredStart, _ => null };
        var end = rule.Mode == "EXPLICIT_RANGE" ? rule.EndDate : declaredEnd;
        if (!Date(start) || !Date(end) || string.CompareOrdinal(start, end) > 0 || string.IsNullOrWhiteSpace(authorityRevision)
            || rule.Mode is not ("EXPLICIT_RANGE" or "TARGET_DATA_WINDOW" or "CUMULATIVE_FROM"))
            return (null, new("AGG_DATA_WINDOW_UNRESOLVED", "$.timeRules[" + rule.Id + "]", "Explicit data bounds and authority revision are required; no due-date fallback."));
        if (rule.Match is not ("CONTAINED" or "OVERLAPS_WHOLE_REPORT")
            || rule.SourceDateBasis is not ("DECLARED_DATA_WINDOW" or "DATE_FIELD" or "INSTANT_FIELD"))
            return (null, new("AGG_TIME_BASIS", "$.timeRules[" + rule.Id + "]", "Select source date basis and whole-report match explicitly."));
        return (new(rule.Id, start!, end!, rule.SourceDateBasis, rule.Match, "Asia/Ho_Chi_Minh", authorityRevision), null);
    }

    internal static AggregateIssueDto? Compare(AggregatePeriodContextDto requested, AggregatePeriodContextDto authoritative)
    {
        if (string.IsNullOrWhiteSpace(authoritative.WorkId) || (authoritative.View?.Kind != "WORK" && string.IsNullOrWhiteSpace(authoritative.AssignmentId))
            || string.IsNullOrWhiteSpace(authoritative.BindingId) || string.IsNullOrWhiteSpace(authoritative.ScheduleRevision)
            || authoritative.Kind is not ("ONCE" or "PERIODIC")
            || (authoritative.Kind == "PERIODIC" && (string.IsNullOrWhiteSpace(authoritative.WorkReportPeriodId)
                || string.IsNullOrWhiteSpace(authoritative.PeriodInstanceKey))))
            return new("AGG_CONTEXT_UNRESOLVED", "$.context", "Authoritative context is incomplete.");
        if (requested != authoritative)
            return new("AGG_CONTEXT_STALE", "$.context", "Context changed; resolve again before preview or mutation.");
        return null;
    }
}
