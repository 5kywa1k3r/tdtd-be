using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.NativeStatisticTests;

internal static class NativeEmptyPlanContentCases
{
    internal static async Task Run(Func<string, Func<Task>, Task> test)
    {
        const string emptyPlan = "{\"version\":2,\"targets\":[]}";
        var table = Fixtures.Table("plainText", "vertical");
        NativeStatisticCalculationSource Source(string hash = Fixtures.Schema, long blankRows = 0,
            string kind = "REPORT_CONTENT_TABLE_V1")
        {
            var pair = Fixtures.Source("plainText", ["A", "B", "C", "D"], layout: "vertical");
            var raw = JsonSerializer.Serialize(new { nativeTables = new { version = 1, schemaHash = hash,
                tables = new[] { new { tableId = "table", records = Array.Empty<object>(),
                    contentRef = new { kind, id = Fixtures.Schema, hash = Fixtures.Schema,
                        rowCount = 2, blankRows, maxContentLength = 5000, maxUnitLength = 30 } } } } });
            var payloadHash = StatConfigCanonicalJson.HashUtf8(raw);
            pair.Report.PayloadHash = payloadHash;
            return NativeStatisticCalculationSource.Capture(pair.Report,
                pair.Payload with { TableValuesJson = raw, PayloadHash = payloadHash });
        }
        var source = Source();
        var pins = new[] { source.Pin };
        NativeStatisticCalculationResult Calculate(IEnumerable<NativeStatisticCalculationSource> sources,
            string plan = emptyPlan, IReadOnlyList<WorkReportNativeSourcePin>? expected = null,
            NativeStatisticCalculationLimits? limits = null, CancellationToken ct = default)
        {
            var selected = expected ?? pins;
            return NativeTableStatisticCalculator.Calculate(plan, [table], Fixtures.Sections, "[]", "[]",
                Fixtures.Schema, selected, WorkReportNativeSourcePin.Digest(selected), sources, limits ?? Fixtures.Limits, ct);
        }
        Task Check(Action action) { action(); return Task.CompletedTask; }
        void Reject(Action action, string? reason = null)
        {
            try { action(); }
            catch (Exception ex) when (ex is AppException or OperationCanceledException)
            {
                if (reason != null && !ex.ToString().Contains(reason, StringComparison.Ordinal)
                    && !(ex is AppException app && JsonSerializer.Serialize(app.Details).Contains(reason, StringComparison.Ordinal)))
                    throw new Exception("Unexpected rejection: " + reason, ex);
                return;
            }
            throw new Exception("Expected rejection.");
        }
        await test("empty native plan retains content reference pins without fabricated groups", () => Check(() => {
            var before = source.Values.GetRawText(); var result = Calculate([source]);
            if (result.Groups.Count != 0 || result.Sources.Single() != source.Pin || source.Values.GetRawText() != before)
                throw new Exception("Empty plan changed source or emitted a calculation.");
        }));
        await test("active native plan still rejects paged content", () => Check(() =>
            Reject(() => Calculate([source], Fixtures.Plan(["COUNT"], layout: "vertical")), "CONTENT_TABLE_REQUIRES_PAGED_READER")));
        await test("empty plan rejects source revision drift", () => Check(() =>
            Reject(() => Calculate([source], expected: [source.Pin with { PayloadRevision = source.Pin.PayloadRevision + 1 }]))));
        await test("empty plan rejects source payload hash drift", () => Check(() =>
            Reject(() => Calculate([source], expected: [source.Pin with { PayloadHash = new string('b', 64) }]))));
        await test("empty plan rejects duplicate membership", () => Check(() => Reject(() => Calculate([source, source]))));
        await test("empty plan rejects incomplete membership", () => Check(() => Reject(() => Calculate([]))));
        await test("empty plan validates content reference shape", () => Check(() => {
            var bad = Source(blankRows: 3); Reject(() => Calculate([bad], expected: [bad.Pin]));
        }));
        await test("empty plan rejects wrong content reference kind", () => Check(() => {
            var bad = Source(kind: "UNKNOWN"); Reject(() => Calculate([bad], expected: [bad.Pin]));
        }));
        await test("empty plan still validates schema envelope", () => Check(() => {
            var bad = Source(new string('b', 64)); Reject(() => Calculate([bad], expected: [bad.Pin]));
        }));
        await test("empty plan enforces source limit", () => Check(() =>
            Reject(() => Calculate([source], limits: Fixtures.Limits with { MaxReports = 0 }))));
        await test("empty plan respects cancellation", () => Check(() =>
            Reject(() => Calculate([source], ct: new CancellationToken(true)))));
    }
}
