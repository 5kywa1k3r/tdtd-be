using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;

// Isolated in-memory checks. No server reference, packages, network, DB or job host.
var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
    count++;
}
const string valid = """
{"schemaVersion":1,"semanticProfile":"REPORT_MAPPING_V1","nodes":[
 {"id":"s","kind":"SOURCE","inputs":[],"outputs":[{"id":"out","valueType":"NUMBER","shape":"SET","memberId":"n","timeRuleId":"w"}],"form":{"formId":"f1","familyId":"family1","versionNo":1,"schemaHash":"h1"},"origin":"DIRECT_CHILD_REPORTS","sourceCardinality":"SET"},
 {"id":"c","kind":"CALCULATION","inputs":[{"id":"in","valueType":"NUMBER","shape":"SET"}],"outputs":[{"id":"out","valueType":"NUMBER","shape":"SINGLE"}],"expressions":[{"portId":"out","expression":{"kind":"CALL","name":"SUM","arguments":[{"kind":"INPUT","ref":"in"}]}}]},
 {"id":"t","kind":"TARGET","inputs":[{"id":"in","valueType":"NUMBER","shape":"SINGLE","memberId":"total"}],"outputs":[],"form":{"formId":"f2","familyId":"family2","versionNo":1,"schemaHash":"h2"}}
],"edges":[{"id":"e1","from":{"nodeId":"s","portId":"out"},"to":{"nodeId":"c","portId":"in"}},{"id":"e2","from":{"nodeId":"c","portId":"out"},"to":{"nodeId":"t","portId":"in"}}],"timeRules":[{"id":"w","mode":"TARGET_DATA_WINDOW","sourceDateBasis":"DECLARED_DATA_WINDOW","match":"CONTAINED"}]}
""";
void Invalid(string name, Action<JsonObject> mutate, string code)
{
    var json = JsonNode.Parse(valid)!.AsObject(); mutate(json);
    var result = AggregateMappingValidator.Parse(json.ToJsonString());
    Check(!result.StructurallyValid && result.Issues.Any(i => i.Code == code), name);
}
Check(AggregateMappingValidator.Parse(valid).StructurallyValid, "valid reusable recipe");
JsonObject InsertFilter(JsonObject recipe, string edgeId, string nodeId, string shape)
{
    var edge = recipe["edges"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == edgeId)!;
    var from = edge["from"]!.DeepClone();
    recipe["nodes"]!.AsArray().Add(JsonNode.Parse($$$"""
      {"id":"{{{nodeId}}}","kind":"FILTER","inputs":[{"id":"in","valueType":"NUMBER","shape":"{{{shape}}}"}],"outputs":[{"id":"out","valueType":"NUMBER","shape":"{{{shape}}}"}],"predicate":{"kind":"BOOLEAN","value":"true"}}
      """));
    edge["from"] = new JsonObject { ["nodeId"] = nodeId, ["portId"] = "out" };
    recipe["edges"]!.AsArray().Add(new JsonObject { ["id"] = "input-" + nodeId, ["from"] = from,
        ["to"] = new JsonObject { ["nodeId"] = nodeId, ["portId"] = "in" } });
    return recipe;
}
var twoSides = InsertFilter(InsertFilter(JsonNode.Parse(valid)!.AsObject(), "e1", "before", "SET"), "e2", "after", "SINGLE");
Check(AggregateMappingValidator.Parse(twoSides.ToJsonString()).StructurallyValid, "one filter on each side of calculation");
var duplicateBefore = InsertFilter(twoSides.DeepClone().AsObject(), "e1", "before-again", "SET");
Check(AggregateMappingValidator.Parse(duplicateBefore.ToJsonString()).Issues.Any(i => i.Code == "AGG_PATH_LIMIT"), "two consecutive filters before calculation rejected");
var duplicateAfter = InsertFilter(twoSides.DeepClone().AsObject(), "e2", "after-again", "SINGLE");
Check(AggregateMappingValidator.Parse(duplicateAfter.ToJsonString()).Issues.Any(i => i.Code == "AGG_PATH_LIMIT"), "two consecutive filters after calculation rejected");
var twoCalculations = JsonNode.Parse(valid)!.AsObject();
twoCalculations["nodes"]!.AsArray().Add(JsonNode.Parse("""
  {"id":"second","kind":"CALCULATION","inputs":[{"id":"in","valueType":"NUMBER","shape":"SINGLE"}],"outputs":[{"id":"out","valueType":"NUMBER","shape":"SINGLE"}],"expressions":[{"portId":"out","expression":{"kind":"INPUT","ref":"in"}}]}
  """));
twoCalculations["edges"]![1]!["from"]!["nodeId"] = "second";
twoCalculations["edges"]!.AsArray().Add(JsonNode.Parse("""{"id":"to-second","from":{"nodeId":"c","portId":"out"},"to":{"nodeId":"second","portId":"in"}}"""));
Check(AggregateMappingValidator.Parse(twoCalculations.ToJsonString()).Issues.Any(i => i.Code == "AGG_PATH_LIMIT"), "one calculation limit remains enforced");
Check(AggregateMappingValidator.Parse(valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":2")).Issues.Any(i => i.Code == "AGG_JSON_DUPLICATE"), "duplicate property rejected");
Check(!AggregateMappingValidator.Parse(null).StructurallyValid, "null document rejected");
Check(!AggregateMappingValidator.Parse(new string(' ', AggregateMappingValidator.MaxBytes + 1)).StructurallyValid, "size budget");
Invalid("unknown property", j => j["actorCanRead"] = true, "AGG_RECIPE_JSON");
Invalid("metadata belongs to source only", j => j["nodes"]![1]!["reportFilter"] = JsonNode.Parse("""{"kinds":["HISTORICAL"]}"""), "AGG_RECIPE_JSON");
foreach (var filter in new[] { """{"fromDate":"2026-01-01"}""", """{"toDate":"2026-01-31"}""", """{"dueFromDate":"2026-01-01"}""", """{"dueToDate":"2026-01-31"}""", "{}" })
{
    var open = JsonNode.Parse(valid)!.AsObject(); open["timeRules"]![0]!["reportFilter"] = JsonNode.Parse(filter);
    Check(AggregateMappingValidator.Parse(open.ToJsonString()).StructurallyValid, "metadata optional inclusive date bounds " + filter);
}
Invalid("metadata reversed range", j => j["timeRules"]![0]!["reportFilter"] = JsonNode.Parse("""{"fromDate":"2026-02-01","toDate":"2026-01-01"}"""), "AGG_REPORT_FILTER_DATE");
Invalid("metadata invalid supplied bound", j => j["timeRules"]![0]!["reportFilter"] = JsonNode.Parse("""{"toDate":"2026-02-30"}"""), "AGG_REPORT_FILTER_DATE");
Invalid("metadata empty string is not a date", j => j["timeRules"]![0]!["reportFilter"] = JsonNode.Parse("""{"fromDate":""}"""), "AGG_REPORT_FILTER_DATE");
Invalid("metadata kinds are explicit", j => j["timeRules"]![0]!["reportFilter"] = JsonNode.Parse("""{"kinds":["UNKNOWN"]}"""), "AGG_REPORT_FILTER_KIND");
Invalid("metadata selections are nonempty", j => j["timeRules"]![0]!["reportFilter"] = JsonNode.Parse("""{"unitIds":[]}"""), "AGG_REPORT_FILTER_VALUES");
Invalid("table cell predicate cannot reference source ports", j => j["nodes"]![1]!["expressions"]![0]!["expression"] = JsonNode.Parse("""{"kind":"TABLE_FILTER","ref":"in","area":{"kind":"ALL"},"columnIndex":1,"predicate":{"kind":"INPUT","ref":"in"}}"""), "AGG_EXPRESSION_REF");
Invalid("header coordinate zero excluded", j => j["nodes"]![1]!["expressions"]![0]!["expression"] = JsonNode.Parse("""{"kind":"TABLE_FILTER","ref":"in","area":{"kind":"ALL"},"columnIndex":0,"predicate":{"kind":"BOOLEAN","value":"true"}}"""), "AGG_TABLE_FILTER_SCHEMA");
var tableRecipe = JsonNode.Parse(valid)!.AsObject();
foreach (var node in tableRecipe["nodes"]!.AsArray()) foreach (var direction in new[] { "inputs", "outputs" })
    foreach (var port in node![direction]!.AsArray()) port!["valueType"] = "TABLE";
tableRecipe["nodes"]![1]!["expressions"]![0]!["expression"] = JsonNode.Parse("""{"kind":"CALL","name":"APPEND_TABLE","arguments":[{"kind":"TABLE_FILTER","ref":"in","area":{"kind":"ALL"},"columnIndex":1,"predicate":{"kind":"CALL","name":"IS_PRESENT","arguments":[{"kind":"INPUT","ref":"cell"}]}}]}""");
tableRecipe["timeRules"]![0]!["reportFilter"] = JsonNode.Parse("""{"fromDate":"2026-01-01","toDate":"2026-01-31","kinds":["HISTORICAL"]}""");
Check(AggregateMappingValidator.Parse(tableRecipe.ToJsonString()).StructurallyValid, "Table filter is part of one calculation with explicit metadata date filter");
Invalid("unknown version", j => j["schemaVersion"] = 2, "AGG_RECIPE_VERSION");
Invalid("null collections", j => j["nodes"] = null, "AGG_RECIPE_REQUIRED");
Invalid("null node", j => j["nodes"]![0] = null, "AGG_NODE_ID");
Invalid("duplicate node", j => j["nodes"]![1]!["id"] = "s", "AGG_NODE_ID");
Invalid("duplicate port", j => j["nodes"]![1]!["outputs"]![0]!["id"] = "in", "AGG_PORT_ID");
Invalid("unknown member time rule", j => j["nodes"]![0]!["outputs"]![0]!["timeRuleId"] = "missing", "AGG_TIME_RULE_REF");
Invalid("set into scalar", j => j["nodes"]![2]!["inputs"]![0]!["valueType"] = "TEXT", "AGG_EDGE_TYPE");
Invalid("unbound input", j => j["edges"]!.AsArray().RemoveAt(1), "AGG_INPUT_UNBOUND");
Invalid("two writers", j => { var edge = j["edges"]![1]!.DeepClone(); edge["id"] = "e3"; j["edges"]!.AsArray().Add(edge); }, "AGG_MULTIPLE_WRITERS");
Invalid("cycle", j => { j["nodes"]![1]!["outputs"]![0]!["shape"] = "SET"; j["edges"]![0]!["from"]!["nodeId"] = "c"; }, "AGG_GRAPH_CYCLE");
Invalid("unknown function", j => j["nodes"]![1]!["expressions"]![0]!["expression"]!["name"] = "eval", "AGG_EXPRESSION_SCHEMA");
Invalid("foreign reference", j => j["nodes"]![1]!["expressions"]![0]!["expression"]!["arguments"]![0]!["ref"] = "secret", "AGG_EXPRESSION_REF");
Invalid("COUNT basis mandatory", j => j["nodes"]![1]!["expressions"]![0]!["expression"]!["name"] = "COUNT", "AGG_COUNT_BASIS");
Invalid("DISTINCT normalization mandatory", j => j["nodes"]![1]!["expressions"]![0]!["expression"]!["name"] = "COUNT_DISTINCT", "AGG_DISTINCT_OPTIONS");
Invalid("CONCAT order mandatory", j => j["nodes"]![1]!["expressions"]![0]!["expression"]!["name"] = "CONCAT", "AGG_CONCAT_OPTIONS");
Invalid("no date fallback", j => j["timeRules"]![0]!["sourceDateBasis"] = "DUE_AT", "AGG_TIME_BASIS");
Invalid("invalid date", j => { j["timeRules"]![0]!["mode"] = "EXPLICIT_RANGE"; j["timeRules"]![0]!["startDate"] = "2026-02-30"; j["timeRules"]![0]!["endDate"] = "2026-03-01"; }, "AGG_TIME_RULE_SCHEMA");
// Numeric literals stay exact strings rather than passing through IEEE754.
var precise = JsonNode.Parse(valid)!.AsObject();
precise["nodes"]![1]!["expressions"]![0]!["expression"] = JsonNode.Parse("""{"kind":"NUMBER","value":"9007199254740993.00000000000000000001"}""");
var parsed = AggregateMappingValidator.Parse(precise.ToJsonString());
Check(parsed.StructurallyValid && parsed.Recipe!.Nodes[1].Expressions![0].Expression.Value == "9007199254740993.00000000000000000001", "exact numeric transport");
precise["nodes"]![1]!["expressions"]![0]!["expression"] = JsonNode.Parse("""{"kind":"INSTANT","value":"2026-09-30T17:30:00Z"}""");
Check(AggregateMappingValidator.Parse(precise.ToJsonString()).StructurallyValid, "UTC instant syntax accepted before P02 type inference");
precise["nodes"]![1]!["expressions"]![0]!["expression"]!["value"] = "09/30/2026 17:30Z";
Check(AggregateMappingValidator.Parse(precise.ToJsonString()).Issues.Any(i => i.Code == "AGG_LITERAL"), "locale dependent instant rejected");
var facts = new AggregateAuthorityFacts(
    Authenticated: true, ContextReadable: true, WholeSourceReadable: true,
    SameWork: true, DirectChild: true, IntermediateSameContext: false,
    CanEditReport: true, CanSubmitReport: true, CanReviewReport: false,
    CanReadLineage: true, MutationScopeOpen: true, TargetActive: true,
    TargetStatus: "Draft", TargetLockedByConsumer: false, ConfigReadable: true,
    IsAssignmentAssignee: true, OwnerScopeMatches: true, DelegationValid: true);
bool Allowed(AggregateAction action, AggregateAuthorityFacts f) => AggregateMappingPolicy.Decide(action, f).Allowed;
Check(Allowed(AggregateAction.EditConfig, facts), "B edits own assignment config");
Check(Allowed(AggregateAction.EditMapping, facts with { ConfigMutationScopeOpen = false }), "exact reopened period can edit its mapping");
Check(!Allowed(AggregateAction.EditConfig, facts with { ConfigMutationScopeOpen = false }), "exact reopened period cannot edit config shared by closed periods");
Check(!Allowed(AggregateAction.EditConfig, facts with { IsAssignmentAssignee = false }), "assigner/form owner alone cannot edit B config");
Check(!Allowed(AggregateAction.EditConfig, facts with { OwnerScopeMatches = false }), "another assignee scope denied");
Check(!Allowed(AggregateAction.ReadSource, facts with { WholeSourceReadable = false }), "form rights never grant report rights");
Check(!Allowed(AggregateAction.ReadSource, facts with { SameWork = false }), "cross-work denied");
Check(!Allowed(AggregateAction.ReadSource, facts with { DirectChild = false }), "grandchild source denied");
Check(Allowed(AggregateAction.ReadSource, facts with { DirectChild = false, IntermediateSameContext = true }), "same-context T allowed");
Check(!Allowed(AggregateAction.ApplyDraft, facts with { TargetStatus = "Submitted" }), "frozen apply denied");
Check(!Allowed(AggregateAction.ApplyDraft, facts with { TargetLockedByConsumer = true }), "source owner lock honored");
Check(!Allowed(AggregateAction.RefreshDraft, facts with { DelegationValid = false }), "revoked delegation denied");
Check(Allowed(AggregateAction.Submit, facts with { WholeSourceReadable = false }), "capability is not completeness gate; submit orchestration must re-resolve");
Check(!Allowed(AggregateAction.Approve, facts with { TargetStatus = "Submitted" }), "assignee not reviewer");
Check(Allowed(AggregateAction.Return, facts with { TargetStatus = "Submitted", CanReviewReport = true }), "immediate reviewer return");
Check(!Allowed(AggregateAction.ApplyDraft, facts with { MutationScopeOpen = false }), "barrier preserved");
foreach (var action in Enum.GetValues<AggregateAction>())
    Check(!Allowed(action, facts with { Authenticated = false }), "unauthenticated " + action);
var context = new AggregatePeriodContextDto("PERIODIC", "w", "a", "b", "r", "p", "pi", "20260930", null, null, "2026-09-30T10:00:00Z", "s1");
Check(AggregatePeriodContextContract.Compare(context, context) == null, "known identity does not fabricate data bounds");
Check(AggregatePeriodContextContract.Compare(context with { BindingId = "other" }, context)?.Code == "AGG_CONTEXT_STALE", "binding mismatch rejected");
Check(AggregatePeriodContextContract.Compare(context, context with { ScheduleRevision = "s2" })?.Code == "AGG_CONTEXT_STALE", "schedule change rejected");
Check(AggregatePeriodContextContract.Compare(context, context with { WorkReportPeriodId = null })?.Code == "AGG_CONTEXT_UNRESOLVED", "periodic requires actual period identity");
var once = context with { Kind = "ONCE", WorkReportPeriodId = null, PeriodInstanceKey = null, PeriodKey = null };
Check(AggregatePeriodContextContract.Compare(once, once) == null, "once requires no fake schedule identity");
var byPeriod = new AggregateTimeRuleDto { Id = "w", Mode = "TARGET_DATA_WINDOW", SourceDateBasis = "DECLARED_DATA_WINDOW", Match = "CONTAINED" };
var september = AggregatePeriodContextContract.ResolveWindow(byPeriod, "2026-09-01", "2026-09-30", "dw1");
var october = AggregatePeriodContextContract.ResolveWindow(byPeriod, "2026-10-01", "2026-10-31", "dw2");
Check(september.Window?.EndDate == "2026-09-30" && october.Window?.EndDate == "2026-10-31", "period windows use declared bounds without fixed day offset");
Check(AggregatePeriodContextContract.ResolveWindow(byPeriod, null, null, "s1").Issue?.Code == "AGG_DATA_WINDOW_UNRESOLVED", "deadline/context identity cannot fill missing data window");
var explicitRule = byPeriod with { Mode = "EXPLICIT_RANGE", StartDate = "2026-09-01", EndDate = "2026-09-15" };
Check(AggregatePeriodContextContract.ResolveWindow(explicitRule, "2026-10-01", "2026-10-31", "recipe1").Window?.EndDate == "2026-09-15", "explicit dates stay fixed across periods");
var cumulative = byPeriod with { Mode = "CUMULATIVE_FROM", StartDate = "2026-01-01", Match = "OVERLAPS_WHOLE_REPORT" };
var cumulativeWindow = AggregatePeriodContextContract.ResolveWindow(cumulative, "2026-09-01", "2026-09-30", "dw1").Window;
Check(cumulativeWindow?.StartDate == "2026-01-01" && cumulativeWindow.EndDate == "2026-09-30", "cumulative resolves selected start through declared period end");
Check(cumulativeWindow?.Match == "OVERLAPS_WHOLE_REPORT", "whole-report overlap choice retained");
Check(AggregatePeriodContextContract.ResolveWindow(cumulative with { StartDate = "2026-10-01" }, "2026-09-01", "2026-09-30", "dw1").Window == null, "reversed cumulative range rejected");
Check(AggregatePeriodContextContract.ResolveWindow(byPeriod, "2026-09-01", "2026-09-30", "").Window == null, "unversioned date authority rejected");
Invalid("no implicit match mode", j => j["timeRules"]![0]!["match"] = null, "AGG_TIME_BASIS");
Invalid("no implicit source window", j => j["nodes"]![0]!["outputs"]![0]!["timeRuleId"] = null, "AGG_TIME_RULE_REQUIRED");
Invalid("relative mode not approved", j => j["timeRules"]![0]!["mode"] = "RELATIVE_RANGE", "AGG_TIME_RULE_SCHEMA");
var tableJson = JsonNode.Parse(valid)!.AsObject();
foreach (var node in tableJson["nodes"]!.AsArray())
{
    foreach (var port in node!["inputs"]!.AsArray().Concat(node["outputs"]!.AsArray())) port!["valueType"] = "TABLE";
}
tableJson["nodes"]![1]!["expressions"]![0]!["expression"]!["name"] = "APPEND_TABLE";
Check(AggregateMappingValidator.Parse(tableJson.ToJsonString()).StructurallyValid, "simple table append wire contract");
tableJson["nodes"]![1]!["expressions"] = new JsonArray();
tableJson["nodes"]![1]!["tableAssignments"] = JsonNode.Parse("""
[{"id":"a1","sourceInputPortId":null,"targetOutputPortId":"out","source":null,
 "target":{"kind":"CELL","rowStart":2,"rowEnd":2,"columnStart":3,"columnEnd":3},
 "expression":{"kind":"CALL","name":"COUNT","arguments":[{"kind":"TABLE_RANGE","ref":"in","area":{"kind":"RANGE","rowStart":1,"rowEnd":10,"columnStart":1,"columnEnd":3}}],"options":{"basis":"PRESENT_ROWS","columnIndex":2}}}]
""");
Check(AggregateMappingValidator.Parse(tableJson.ToJsonString()).StructurallyValid, "row count writes a table cell at explicit coordinates");
tableJson["nodes"]![1]!["tableAssignments"]![0]!["expression"]!["options"]!["columnIndex"] = null;
Check(AggregateMappingValidator.Parse(tableJson.ToJsonString()).Issues.Any(i => i.Code == "AGG_COUNT_COLUMN"), "row presence requires selected column");
tableJson["nodes"]![1]!["tableAssignments"]![0]!["target"]!["rowStart"] = 0;
Check(AggregateMappingValidator.Parse(tableJson.ToJsonString()).Issues.Any(i => i.Code == "AGG_TABLE_AREA"), "coordinates are one-based");
Console.WriteLine($"PASS: {count} isolated P01 contract checks. No evaluator, API, DB, job or product acceptance exercised.");
