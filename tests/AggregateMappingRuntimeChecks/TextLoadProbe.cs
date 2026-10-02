using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;

// An isolated measurement of the existing evaluator, not an end-to-end capacity test.
internal static class TextLoadProbe
{
    internal static void Run()
    {
        const int sourceCount = 166;
        const string separator = "\n\n", phrase = "Nội dung báo cáo tiếng Việt 📝.\n";
        var expression = new AggregateExpressionDto { Kind = "CALL", Name = "CONCAT",
            Arguments = [new() { Kind = "INPUT", Ref = "in" }],
            Options = new() { Trim = false, Separator = separator, Order = "UNIT_THEN_PERIOD" } };
        Console.WriteLine("TEXT_PROBE existing evaluator only; no DB/API/browser; allocations are thread totals, not peak memory; limits unchanged.");
        foreach (var size in new[] { 1_000, 10_000, 50_000 })
        {
            var items = Enumerable.Range(1, sourceCount).Select(i => {
                var prefix = i.ToString("D3") + ": ";
                var text = prefix + string.Concat(Enumerable.Repeat(phrase, (size - prefix.Length) / phrase.Length));
                text = text.PadRight(size, ' '); // Never split the emoji surrogate pair.
                return new AggregateObservation(new("TEXT", "VALUE", Text: text), [new("report" + i, "unit" + i.ToString("D3"), "ONCE", "body")]);
            }).ToArray();
            var inputs = new Dictionary<string, AggregateChannel> { ["in"] = new("TEXT", "SET", items.Reverse().ToArray()) };
            var expectedLength = (long)sourceCount * size + (sourceCount - 1) * separator.Length;
            using var expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var i = 0; i < items.Length; i++)
            {
                if (i > 0) expectedHash.AppendData(Encoding.UTF8.GetBytes(separator));
                expectedHash.AppendData(Encoding.UTF8.GetBytes(items[i].Value.Text!));
            }
            var bytesBefore = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
            try
            {
                var result = AggregateEvaluator.Scalar(new AggregateEvaluator(new AggregateBudget(default), [])
                    .Evaluate(expression, inputs));
                watch.Stop(); var allocated = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
                var text = result.Value.Text!;
                if (text.Length != expectedLength || !SHA256.HashData(Encoding.UTF8.GetBytes(text)).SequenceEqual(expectedHash.GetHashAndReset())
                    || result.Trace.Count != sourceCount) throw new InvalidOperationException("Long text order/content/trace changed");
                Console.WriteLine($"MEASURE sourceReports={sourceCount} utf16UnitsPerField={size} outputUtf16Units={text.Length} utf8Bytes={Encoding.UTF8.GetByteCount(text)} jsonWireBytes={Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(text))} evaluatorMs={watch.Elapsed.TotalMilliseconds:F3} allocatedBytes={allocated} status=EXACT");
            }
            catch (AggregatePreviewException ex) when (ex.Code == "AGG_BUDGET_EXCEEDED")
            {
                watch.Stop();
                Console.WriteLine($"LIMIT sourceReports={sourceCount} utf16UnitsPerField={size} expectedOutputUtf16Units={expectedLength} evaluatorMs={watch.Elapsed.TotalMilliseconds:F3} allocatedBytes={GC.GetAllocatedBytesForCurrentThread() - bytesBefore} code={ex.Code} path={ex.Path}");
            }
        }
        Console.WriteLine("PROBE COMPLETE; LIMIT rows remain unsupported workloads, not passing capacity checks.");
    }
}
