using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class NativeUntouchedTargetChecks
{
    internal static void Run()
    {
        var form = new DynamicFormTemplate { NativeTablesVersion = 2, FieldsJson = "[]", PublishedSchemaHash = "pinned",
            TablesJson = """
            [{"id":"items","layout":"vertical","fields":[{"id":"name"}],"rows":[]},
             {"id":"matrix","layout":"matrix","fields":[{"id":"score"}],"rows":[{"id":"r1"},{"id":"r2"}]}]
            """ };
        var schema = new AggregateSchema(new("f", "f", 1, "pinned"), new Dictionary<string, AggregateMember> {
            ["items"] = new("items", "LIST", List: new([new("name", "TEXT")])),
            ["matrix"] = new("matrix", "TABLE", new("m", "matrix", ["score"], ["r1", "r2"], [["NUMBER"], ["NUMBER"]])) });
        WorkReportPayloadSnapshot Payload(string? tables, int revision = 0) => new("[]", "{}", tables, null, revision, null, 0, "READY", false, true);
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
        foreach (var raw in new string?[] { null, "", " {} " })
        {
            var values = AggregateNativePayloadAdapter.Read(form, Payload(raw), schema, null);
            Check(values.Count == 2 && values.Values.All(v => v.State == "NO_RESULT" && v.List == null && v.Table == null),
                "untouched target absence is NO_RESULT, never fake empty List/Table");
        }
        Check(AggregateNativePayloadAdapter.IsUninitializedTarget(Payload(null, 1)), "InitDraft's first empty payload revision is recognized");
        Check(!AggregateNativePayloadAdapter.IsUninitializedTarget(Payload(null, 2)), "saved payload absence cannot be repaired implicitly");
        Check(!AggregateNativePayloadAdapter.IsUninitializedTarget(Payload(null, 1) with { FieldValuesJson = "{\"score\":3}" }), "entered scalar values are not an untouched draft");
        Check(!AggregateNativePayloadAdapter.IsUninitializedTarget(Payload("{\"nativeTables\":{}}")), "malformed explicit native payload stays strict");
        var source = new AggregateSourceHeader(new("w", "a", "b", null, null, "r", 1, 0, 0, "hash", "pinned", "Approved", true, "rel", "auth"),
            "parent", schema.Pin, "u", "once", true, true, false, null);
        foreach (var (payload, header) in new[] { (Payload(null), source), (Payload(null, 2), (AggregateSourceHeader?)null) })
        {
            try { _ = AggregateNativePayloadAdapter.Read(form, payload, schema, header); throw new Exception("Missing native payload accepted"); }
            catch (AppException ex)
            {
                var details = JsonSerializer.SerializeToElement(ex.Details);
                Check(ex.Code == AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID
                    && details.GetProperty("reason").GetString() == "NATIVE_VALUES_EXPLICIT_INPUT_REQUIRED",
                    "Approved source and saved target still rejected by authoritative native validator");
            }
        }
        using var doc = JsonDocument.Parse(AggregateViewPayload.EmptyTables(form).ToJsonString());
        var native = doc.RootElement.GetProperty("nativeTables");
        var tables = native.GetProperty("tables");
        Check(native.GetProperty("schemaHash").GetString() == "pinned" && tables.GetArrayLength() == 2,
            "first Apply initializer carries pinned hash and every native table");
        Check(tables[0].GetProperty("records").GetArrayLength() == 0 && tables[1].GetProperty("rows")[1].GetProperty("rowId").GetString() == "r2"
            && tables[1].GetProperty("rows")[0].GetProperty("cells").EnumerateObject().Count() == 0,
            "initializer preserves matrix row identities and unentered cells");
    }
}
