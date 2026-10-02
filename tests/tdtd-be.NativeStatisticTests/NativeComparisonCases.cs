using System.Text.Json;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.StatisticsReconciliation;

namespace tdtd_be.NativeStatisticTests;

internal static class NativeComparisonCases
{
    internal static async Task Run(Func<string, Func<Task>, Task> test, string wire)
    {
        var response = JsonSerializer.Deserialize<AdvancedNativeSummaryResponse>(await File.ReadAllTextAsync(wire), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var owner = response.Configuration.OwnerId.Split(':');
        var input = new NativeComparisonInput(new(owner[0], owner[1], response.SectionId, response.Grain, response.GrainKey,
            response.TimeAxis, response.StartUtc, response.EndExclusiveUtc, response.Configuration, response.SourceSetHash,
            response.NativeMetadata!.ContentHash), response.Native);
        Task Case(string name, Action action) => test(name, () => { action(); return Task.CompletedTask; });
        static void Check(bool ok) { if (!ok) throw new Exception("Comparison assertion failed"); }
        NativeComparisonInput Change(string method, Func<NativeStatisticOperationDto, NativeStatisticOperationDto> change)
        {
            var selected = input.Result.Groups.First(g => g.Operations.Any(o => o.Method == method));
            var op = selected.Operations.First(o => o.Method == method);
            return input with { Result = input.Result with { Groups = input.Result.Groups.Select(g => g == selected
                ? g with { Operations = g.Operations.Select(o => o == op ? change(o) : o).ToArray() } : g).ToArray() } };
        }
        void Changed(NativeComparisonInput other, string reason = "OPERATION_CHANGED") {
            var result = NativeStatisticSnapshotComparer.Compare(input, other);
            Check(result.Comparable && !result.Equivalent && result.Differences.Any(d => d.Reason == reason));
        }
        void Rejected(NativeComparisonInput other) {
            try { NativeStatisticSnapshotComparer.Compare(input, other); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Invalid shape was accepted");
        }
        await Case("comparison exact HTTP RANGE capture", () => Check(NativeStatisticSnapshotComparer.Compare(input, input).Equivalent));
        await Case("group/operation enumeration order does not change identity", () => {
            var reversed = input with { Result = input.Result with { Groups = input.Result.Groups.Reverse().Select(g => g with { Operations = g.Operations.Reverse().ToArray() }).ToArray() } };
            Check(NativeStatisticSnapshotComparer.Compare(input, reversed).Equivalent);
        });
        foreach (var kind in new[] { "scope", "form", "section", "range", "config", "metadata", "source-set", "schema", "plan", "source-order", "payload" })
            await Case("incomparable on " + kind + " drift", () => {
                var other = kind switch {
                    "scope" => input with { Identity = input.Identity with { ScopeAssignmentId = "other" } },
                    "form" => input with { Identity = input.Identity with { DynamicFormTemplateId = "other" } },
                    "section" => input with { Identity = input.Identity with { SectionId = "other" } },
                    "range" => input with { Identity = input.Identity with { GrainKey = "2026-09-16..2026-09-18" } },
                    "config" => input with { Identity = input.Identity with { Configuration = input.Identity.Configuration with { Revision = input.Identity.Configuration.Revision + 1 } } },
                    "metadata" => input with { Identity = input.Identity with { MetadataHash = new string('a', 64) } },
                    "source-set" => input with { Identity = input.Identity with { SourceSetHash = new string('a', 64) } },
                    "schema" => input with { Result = input.Result with { SchemaHash = new string('a', 64) } },
                    "plan" => input with { Result = input.Result with { PlanContentDigest = new string('a', 64) } },
                    "source-order" => input with { Result = input.Result with { SourceOrderDigest = new string('a', 64) } },
                    _ => input with { Result = input.Result with { Sources = input.Result.Sources.Select((s,i) => i == 0 ? s with { PayloadRevision = s.PayloadRevision + 1 } : s).ToArray() } }
                };
                var result = NativeStatisticSnapshotComparer.Compare(input, other); Check(!result.Comparable && !result.Equivalent);
            });
        var first = input.Result.Groups[0];
        await Case("missing operation is not zero", () => Changed(input with { Result = input.Result with { Groups = input.Result.Groups.Skip(1).ToArray() } }, "MISSING_OPERATION"));
        await Case("unexpected operation preserves its address", () => {
            var extra = first with { Address = first.Address with { TargetId = "unexpected" } };
            Changed(input with { Result = input.Result with { Groups = input.Result.Groups.Append(extra).ToArray() } }, "UNEXPECTED_OPERATION");
        });
        await Case("duplicate group rejected", () => Rejected(input with { Result = input.Result with { Groups = input.Result.Groups.Append(first).ToArray() } }));
        await Case("duplicate operation rejected", () => Rejected(input with { Result = input.Result with { Groups = [first with { Operations = first.Operations.Append(first.Operations[0]).ToArray() }] } }));
        await Case("difference limit fails explicitly instead of partial equivalent", () => {
            var extras = Enumerable.Range(0, 257).Select(i => first with { Address = first.Address with { TargetId = "extra-" + i }, Operations = [first.Operations[0]] });
            Rejected(input with { Result = input.Result with { Groups = input.Result.Groups.Concat(extras).ToArray() } });
        });
        await Case("same numeric text with changed value type differs", () => Changed(Change("SUM", o => o with { Value = o.Value! with { Type = "plainText" } })));
        await Case("CONCAT empty text and missing text are distinct", () => {
            var empty = Change("CONCAT", o => o with { TextChunks = [new(0, "")] });
            var absent = Change("CONCAT", o => o with { TextChunks = null });
            Check(!NativeStatisticSnapshotComparer.Compare(empty, absent).Equivalent);
        });
        await Case("group identity keeps null distinct from empty", () => {
            var other = input with { Result = input.Result with { Groups = input.Result.Groups.Select(g => g == first ? g with { Address = g.Address with { RecordId = "" } } : g).ToArray() } };
            Changed(other, "MISSING_OPERATION");
        });
        await Case("AVG equal quotient with different sum/count is different", () => Changed(Change("AVG", o => {
            Check(o.Numeric!.Sum == "12" && o.Numeric.Count == 2);
            return o with { Numeric = o.Numeric with { Sum = "24", Count = 4 } };
        })));
        await Case("SUM exact tiny decimal difference retained", () => Changed(Change("SUM", o => o with { Value = o.Value! with { Text = "12.0000000000000000000000000001" } })));
        await Case("DISTINCT wrong daily count sum detected", () => Changed(Change("DISTINCT_COUNT", o => o with { Value = o.Value! with { Text = "99" } })));
        await Case("missing/null counters cannot be interchanged", () => Changed(Change("COUNT", o => o with { Counts = o.Counts with { MissingCount = 1 } })));
        await Case("CONCAT LF/CRLF difference detected", () => Changed(Change("CONCAT", o => o with { TextChunks = o.TextChunks!.Select(c => c with { Text = c.Text.Replace("\n", "\r\n") }).ToArray() })));
        await Case("CONCAT source provenance change detected", () => Changed(Change("CONCAT", o => o with { TextSegments = o.TextSegments!.Select((s,i) => i == 0 ? s with { Source = s.Source with { ReportId = "other" } } : s).ToArray() })));
        await Case("CONCAT rechunking preserves logical text", () => {
            var other = Change("CONCAT", o => { var text = string.Concat(o.TextChunks!.Select(c => c.Text)); return o with { TextChunks = [new(0, text[..1]), new(1, text[1..])] }; });
            Check(NativeStatisticSnapshotComparer.Compare(input, other).Equivalent);
        });
        await Case("invalid chunk index rejected", () => Rejected(Change("CONCAT", o => o with { TextChunks = [new(1, "text")] })));
        foreach (var method in new[] { "STACK_ROWS", "STACK_COLUMNS" }) {
            await Case(method + " geometry mismatch", () => Changed(Change(method, o => o with { Stack = o.Stack! with { Columns = o.Stack.Columns + 1 } })));
            await Case(method + " rechunking unchanged", () => {
                var other = Change(method, o => { var cells = o.Stack!.Chunks.SelectMany(c => c.Cells).ToArray(); return o with { Stack = o.Stack with { Chunks = [new(0,cells[..1]), new(1,cells[1..])] } }; });
                Check(NativeStatisticSnapshotComparer.Compare(input, other).Equivalent);
            });
            await Case(method + " cell type and provenance retained", () => Changed(Change(method, o => o with { Stack = o.Stack! with { Chunks = o.Stack.Chunks.Select(c => c with { Cells = c.Cells.Select((cell,i) => i == 0 ? cell with { Value = cell.Value with { Type = "plainText" }, Source = cell.Source with { FieldId = "other" } } : cell).ToArray() }).ToArray() } })));
        }
    }
}
