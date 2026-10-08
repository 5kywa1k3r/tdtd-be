using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

// Opt-in recipe 3. Existing function names and their blank/denominator rules are unchanged.
internal static class AggregateExtendedFunctions
{
    internal static readonly HashSet<string> Names = new(StringComparer.Ordinal)
        { "DATE_MIN", "DATE_MAX", "CHOICE_UNION", "CHOICE_INTERSECTION", "AVG_PRESENT", "LEN", "TRIM", "REPORT_COUNT", "COUNT_CHOICE", "CONCAT_UNIT", "CHOICE_COUNT_TABLE" };
    internal sealed record Signature(string Name, string[] InputTypes, string[] InputShapes, string OutputType, string OutputShape, string[] RequiredOptions);
    internal static readonly Signature[] Signatures = [
        new("DATE_MIN", ["DATE_ONLY"], ["SET","SINGLE"], "DATE_ONLY", "SINGLE", []),
        new("DATE_MAX", ["DATE_ONLY"], ["SET","SINGLE"], "DATE_ONLY", "SINGLE", []),
        new("CHOICE_UNION", ["CHOICE_MANY"], ["SET","SINGLE"], "CHOICE_MANY", "SINGLE", []),
        new("CHOICE_INTERSECTION", ["CHOICE_MANY"], ["SET","SINGLE"], "CHOICE_MANY", "SINGLE", []),
        new("AVG_PRESENT", ["NUMBER"], ["SET","SINGLE"], "NUMBER", "SINGLE", []),
        new("LEN", ["TEXT"], ["SINGLE"], "NUMBER", "SINGLE", []),
        new("TRIM", ["TEXT"], ["SINGLE"], "TEXT", "SINGLE", []),
        new("REPORT_COUNT", ["NUMBER","TEXT","BOOLEAN","DATE_ONLY","DATE_PARTIAL","CHOICE_ONE","CHOICE_MANY","TABLE","LIST"], ["SET","SINGLE"], "NUMBER", "SINGLE", ["basis"]),
        new("COUNT_CHOICE", ["CHOICE_ONE","CHOICE_MANY"], ["SET"], "NUMBER", "SINGLE", []),
        new("CONCAT_UNIT", ["TEXT"], ["SET"], "TEXT", "SINGLE", []),
        new("CHOICE_COUNT_TABLE", ["CHOICE_ONE","CHOICE_MANY"], ["SET"], "TABLE", "SINGLE", []),
        new("WEIGHTED_AVG", ["LIST"], ["SET","SINGLE"], "NUMBER", "SINGLE", ["valueFieldId","weightFieldId"]),
        new("COUNT_DISTINCT_FIELD", ["LIST"], ["SET","SINGLE"], "NUMBER", "SINGLE", ["valueFieldId","trim/caseSensitive for TEXT"]),
        new("LIST_MERGE_VERTICAL", ["LIST"], ["SET","SINGLE"], "LIST", "SINGLE", ["inputs.project"]),
        new("LIST_MERGE_HORIZONTAL", ["LIST"], ["SET","SINGLE"], "LIST", "SINGLE", ["inputs.project"]) ];
    internal static AggregateExpressionType Infer(string name, AggregateExpressionType input)
    {
        var valid = name switch {
            "DATE_MIN" or "DATE_MAX" => input.Type == "DATE_ONLY",
            "CHOICE_UNION" or "CHOICE_INTERSECTION" => input.Type == "CHOICE_MANY",
            "AVG_PRESENT" => input.Type == "NUMBER",
            "LEN" or "TRIM" => input.Type == "TEXT" && input.Shape == "SINGLE",
            "COUNT_CHOICE" => input.Type is "CHOICE_ONE" or "CHOICE_MANY" && input.Shape == "SET",
            "CONCAT_UNIT" => input.Type == "TEXT" && input.Shape == "SET",
            "CHOICE_COUNT_TABLE" => input.Type is "CHOICE_ONE" or "CHOICE_MANY" && input.Shape == "SET",
            "REPORT_COUNT" => true, _ => false };
        if (!valid) throw new AggregatePreviewException("AGG_EXPRESSION_TYPE");
        return new(name is "DATE_MIN" or "DATE_MAX" ? "DATE_ONLY"
            : name is "CHOICE_UNION" or "CHOICE_INTERSECTION" ? "CHOICE_MANY"
            : name is "TRIM" or "CONCAT_UNIT" ? "TEXT" : name == "CHOICE_COUNT_TABLE" ? "TABLE" : "NUMBER", "SINGLE",
            name == "CHOICE_COUNT_TABLE" || name != "REPORT_COUNT" && input.TableOrigin);
    }
    internal static AggregateChannel Evaluate(string name, AggregateChannel input, AggregateFunctionOptionsDto? options,
        AggregateBudget budget)
    {
        var type = Infer(name, new(input.Type, input.Shape, input.TableOrigin, input.ListSchema));
        if (name == "CHOICE_COUNT_TABLE") return AggregateChoiceCountTable.Build(input, budget);
        if (name is "LEN" or "TRIM") AggregateTextPolicy.CheckValueOperation(name, input);
        var all = input.Items.Where(i => i.Value.State != "NO_RESULT").ToArray();
        if (all.Any(i => i.Value.State is not ("VALUE" or "BLANK"))) throw new AggregatePreviewException("AGG_SOURCE_VALUE_INVALID");
        var trace = all.SelectMany(i => i.Trace).ToArray();
        budget.Spend(all.Length, trace.Length * 64L);
        AggregateChannel Result(AggregateValue value) => AggregateEvaluator.Single(value, trace, type.TableOrigin)
            with { EligibleSources = input.EligibleSources };
        if (name == "REPORT_COUNT")
        {
            if (options?.Basis is not ("ELIGIBLE_SOURCES" or "SELECTED_ELEMENTS")) throw new AggregatePreviewException("AGG_COUNT_BASIS_REQUIRED");
            if (input.Type == "LIST")
            {
                var selected = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in input.Items)
                {
                    if (item.Value.State != "VALUE" || item.Value.List == null) throw new AggregatePreviewException("AGG_LIST_SOURCE_UNAVAILABLE");
                    foreach (var row in item.Value.List.Records)
                    { budget.Spend(); foreach (var origin in row.Cells.Values.SelectMany(c => c.Trace)) selected.Add(origin.ReportId); }
                }
                var listOrigins = options?.Basis == "ELIGIBLE_SOURCES" ? input.EligibleSources : input.EligibleSources.Where(t => selected.Contains(t.ReportId)).ToArray();
                var count = options?.Basis == "ELIGIBLE_SOURCES" ? listOrigins.Select(t => t.ReportId).Distinct(StringComparer.Ordinal).LongCount() : selected.Count;
                return AggregateEvaluator.Single(AggregateValue.Numeric(AggregateNumber.From(count)), listOrigins) with { EligibleSources = input.EligibleSources };
            }
            var origins = options?.Basis == "ELIGIBLE_SOURCES" ? input.EligibleSources : trace;
            return Result(AggregateValue.Numeric(AggregateNumber.From(origins.Select(t => t.ReportId)
                .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).LongCount())));
        }
        if (name == "CONCAT_UNIT")
        {
            var parts = new List<string>();
            var contributors = new List<AggregateTrace>();
            foreach (var item in all.OrderBy(i => i.SourceNote?.UnitId, StringComparer.Ordinal)
                .ThenBy(i => i.SourceNote?.OccurrenceKey, StringComparer.Ordinal)
                .ThenBy(i => i.SourceNote?.ReportId, StringComparer.Ordinal))
            {
                budget.Spend();
                if (item.Value.State != "VALUE") continue;
                var content = AggregateTextProjection.VisibleText(item.Value);
                if (content.Length == 0) continue;
                var unit = item.SourceNote?.UnitFullName ?? item.SourceNote?.UnitName;
                if (string.IsNullOrWhiteSpace(unit)) throw new AggregatePreviewException("AGG_UNIT_NAME_UNAVAILABLE");
                parts.Add(unit + " - " + content);
                contributors.AddRange(item.Trace);
            }
            var length = parts.Sum(p => (long)p.Length) + Math.Max(0, parts.Count - 1) * (long)AggregateTextPolicy.BlockSeparator.Length;
            budget.Spend(bytes: length * 4);
            return AggregateEvaluator.Single(parts.Count == 0 ? AggregateValue.NoResult("TEXT")
                : new("TEXT", "VALUE", Text: string.Join(AggregateTextPolicy.BlockSeparator, parts)), contributors)
                with { EligibleSources = input.EligibleSources };
        }
        if (name is "LEN" or "TRIM")
        {
            var value = AggregateEvaluator.Scalar(input).Value;
            if (value.State != "VALUE") return Result(new(type.Type, value.State));
            if (name == "TRIM") return Result(value with { Text = value.Text!.Trim(), LengthText = value.LengthText?.Trim() });
            var text = PlainText(value);
            budget.Spend(bytes: text.Length * 2L);
            return Result(AggregateValue.Numeric(AggregateNumber.From(StringInfo.ParseCombiningCharacters(text).Length)));
        }
        var present = all.Where(i => i.Value.State == "VALUE").ToArray();
        if (all.Length == 0) return Result(AggregateValue.NoResult(type.Type));
        if (name is "CHOICE_UNION" or "CHOICE_INTERSECTION")
        {
            HashSet<string>? set = null;
            foreach (var item in all)
            {
                budget.Spend();
                var codes = item.Value.State == "BLANK" ? [] : item.Value.Choices!;
                if (set == null) set = new(codes, StringComparer.Ordinal);
                else if (name == "CHOICE_UNION") set.UnionWith(codes); else set.IntersectWith(codes);
            }
            return Result(new("CHOICE_MANY", "VALUE", Choices: set!.Order(StringComparer.Ordinal).ToArray()));
        }
        if (present.Length == 0) return Result(AggregateValue.NoResult(type.Type));
        if (name is "DATE_MIN" or "DATE_MAX")
        {
            var dates = present.Select(i => AggregateTimeResolver.Date(i.Value.Text!)).ToArray();
            return Result(new("DATE_ONLY", "VALUE", Text: (name == "DATE_MIN" ? dates.Min() : dates.Max()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        var sum = present.Aggregate(AggregateNumber.From(0), (n, i) => n.Add(i.Value.Number!));
        return Result(AggregateValue.Numeric(sum.Divide(AggregateNumber.From(present.Length))));
    }
    internal static string PlainText(AggregateValue value)
    {
        if (value.LengthText != null) return value.LengthText;
        var text = value.Text!;
        if (value.TextFormat != "RICH_HTML" || !text.Contains('<')) return text;
        // Native writer accepts canonical XML-compatible HTML. Never parse plain text as markup.
        try
        {
            using var reader = XmlReader.Create(new StringReader("<root>" + text + "</root>"), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root!.Value;
        }
        catch (XmlException) { throw new AggregatePreviewException("AGG_RICH_TEXT_INVALID"); }
    }
}
