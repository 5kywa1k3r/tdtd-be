using tdtd_be.Services.WorkAssignments.Domain;
using tdtd_be.Common.Errors;
using N = tdtd_be.Services.WorkAssignments.Domain.WorkAssignmentAutoApproveConditionNormalizer;
var count = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); count++; }
void Reject(string json, string fields) { try { N.NormalizeOrNull(json, fields); throw new Exception("Accepted invalid config: " + json); } catch (AppException) { count++; } }
const string fields = """[{"id":"score","key":"score","type":"number"},{"id":"tags","key":"tags","type":"multiSelect"}]""";
var always = N.NormalizeOrNull("""{"enabled":true}""", fields);
Check(N.Matches(always, null), "legacy explicit always");
Check(N.NormalizeOrNull("""{"enabled":false}""", fields) == null, "off serialize");
Check(!N.Matches(null, null), "off submit");
foreach (var bad in new[] { "{}", "{\"enabled\":\"false\"}", "{\"enabled\":true,\"operator\":\"gte\"}", "{\"enabled\":true,\"fieldId\":4}" }) { Reject(bad, fields); Check(!N.Matches(bad, null), "invalid fail closed"); }
Reject("""{"enabled":true,"fieldId":"gone","fieldKey":"score","value":0}""", fields);
Reject("""{"enabled":true,"fieldId":"score","fieldType":"singleSelect","value":0}""", fields);
var rule = N.NormalizeOrNull("""{"enabled":true,"fieldId":"score","operator":"gte","value":0}""", fields);
Check(N.Matches(rule, """{"values":{"score":0}}"""), "zero");
Check(N.Matches(rule, """{"score":5}"""), "met");
foreach (var payload in new[] { "{}", "{\"score\":null}", "{\"score\":false}", "{\"score\":-1}" }) Check(!N.Matches(rule, payload), "not met");
var notEmpty = N.NormalizeOrNull("""{"enabled":true,"fieldId":"score","operator":"notEmpty"}""", fields);
Check(N.Matches(notEmpty, """{"score":0}"""), "nonempty zero");
Check(!N.Matches(notEmpty, """{"score":false}"""), "nonempty invalid false");
Check(!N.Matches("""{"enabled":true,"fieldId":"score","fieldType":"number","operator":"garbage"}""", """{"score":8}"""), "bad operator does not throw");
var tags = N.NormalizeOrNull("""{"enabled":true,"fieldId":"tags","operator":"neq","value":"A"}""", fields);
Check(!N.Matches(tags, """{"tags":{}}"""), "invalid array cannot satisfy neq");
Check(N.Matches(tags, """{"tags":["B"]}"""), "valid neq");
Console.WriteLine($"PASS {count} isolated checks (no server/DB/API)");
