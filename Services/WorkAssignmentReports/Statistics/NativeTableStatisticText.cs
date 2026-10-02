using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

internal static class NativeStatisticText
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static int Utf8Bytes(string value)
    {
        try { return Utf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw NativeStatisticCalculationError.Invalid("TEXT_INVALID_UNICODE"); }
    }

    internal static NativeStatisticValue Read(JsonElement cell, string type)
    {
        if (cell.ValueKind == JsonValueKind.Undefined) return new(type, "missing");
        var value = cell.GetProperty("value"); // The full L4 envelope validator ran first.
        if (value.ValueKind == JsonValueKind.Null) return new(type, "null");
        return type switch
        {
            "number" => new(type, "value", value.GetDecimal().ToString("G29", CultureInfo.InvariantCulture)),
            "boolean" => new(type, "value", Boolean: value.GetBoolean()),
            "stringList" or "multiSelect" => new(type, "value", Items: value.EnumerateArray().Select(v => v.GetString()!).ToArray()),
            "plainText" or "date" or "fullDate" or "singleSelect" => new(type, "value", value.GetString()),
            _ => throw NativeStatisticCalculationError.Invalid("CALCULATION_TYPE_UNSUPPORTED")
        };
    }

    internal static string TypedKey(NativeStatisticValue value)
        => StatConfigCanonicalJson.Canonicalize(value);

    internal static string Render(NativeStatisticValue value, DynamicFormNativeCellSpecDto spec,
        JsonElement options, NativeStatisticCalculationBudget budget)
    {
        if (options.GetProperty("format").GetString() == "text" && value.Type is not ("plainText" or "stringList"))
            throw NativeStatisticCalculationError.Invalid("CONCAT_TEXT_TYPE_MISMATCH");
        string Choice(string code) => options.GetProperty("choiceFormat").GetString() == "code" ? code
            : spec.Options!.Single(option => option.Code == code).Label!;
        IEnumerable<string> items = value.Type switch
        {
            "multiSelect" => value.Items!.Select(Choice),
            "stringList" => value.Items!,
            "singleSelect" => new[] { Choice(value.Text!) },
            "boolean" => new[] { value.Boolean!.Value ? "true" : "false" },
            _ => new[] { value.Text! }
        };
        var separator = options.GetProperty("itemSeparator").GetString()!;
        var segments = items.ToArray();
        var bytes = segments.Sum(item => (long)Utf8Bytes(item)) + Math.Max(0, segments.Length - 1L) * Utf8Bytes(separator);
        // Check the full expansion before allocating a possibly much larger joined list.
        if (bytes > budget.Limits.MaxRetainedBytes) throw NativeStatisticCalculationError.Invalid("CONCAT_CELL_EXPANSION_LIMIT");
        return string.Join(separator, segments);
    }

    // Partial dates are comparable only at the same precision, without inventing a day.
    internal static (int Precision, int Key) DateKey(string literal)
    {
        var parts = literal.Split('/');
        int Part(int index) => int.Parse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture);
        return parts.Length switch
        {
            1 => (1, Part(0)),
            2 => (2, Part(1) * 100 + Part(0)),
            3 => (3, Part(2) * 10000 + Part(1) * 100 + Part(0)),
            _ => throw NativeStatisticCalculationError.Invalid("DATE_LITERAL_INVALID")
        };
    }

    internal static long RetainedValueBytes(NativeStatisticValue value)
        => 96L + (value.Text is null ? 0 : 2L * value.Text.Length + Utf8Bytes(value.Text))
            + (value.Items?.Sum(item => 32L + 2L * item.Length + Utf8Bytes(item)) ?? 0);
}

// Fixed scale accommodates every validated native decimal. BigInteger avoids
// decimal addition silently losing low digits when large and small values mix.
internal sealed class NativeStatisticExactTotal
{
    private BigInteger _scaled;
    internal long Count { get; private set; }
    internal void Add(string text)
    {
        var number = decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        var nextCount = checked(Count + 1);
        var bits = decimal.GetBits(number);
        var coefficient = (BigInteger)(uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
        var scale = (bits[3] >> 16) & 0xff;
        if ((bits[3] & int.MinValue) != 0) coefficient = -coefficient;
        _scaled += coefficient * BigInteger.Pow(10, 28 - scale);
        Count = nextCount;
    }
    // Only for disjoint, already-authorized child source sets. This does not
    // authorize merging published summaries or deduplicate overlapping reports.
    internal void Merge(NativeStatisticNumericSummary summary)
    {
        var text = summary.Sum;
        if (summary.Count < 0 || string.IsNullOrEmpty(text)) throw NativeStatisticCalculationError.Invalid("NUMERIC_SUMMARY_INVALID");
        var negative = text[0] == '-';
        var magnitude = negative ? text[1..] : text;
        var parts = magnitude.Split('.');
        if (parts.Length is < 1 or > 2 || parts.Any(part => part.Length == 0 || part.Any(c => c is < '0' or > '9'))
            || parts.Length == 2 && parts[1].Length > 28)
            throw NativeStatisticCalculationError.Invalid("NUMERIC_SUMMARY_INVALID");
        var scale = parts.Length == 2 ? parts[1].Length : 0;
        var coefficient = BigInteger.Parse(string.Concat(parts), NumberStyles.None, CultureInfo.InvariantCulture);
        if (summary.Count == 0 && !coefficient.IsZero) throw NativeStatisticCalculationError.Invalid("NUMERIC_SUMMARY_INVALID");
        var nextCount = checked(Count + summary.Count);
        _scaled += (negative ? -coefficient : coefficient) * BigInteger.Pow(10, 28 - scale);
        Count = nextCount;
    }
    internal NativeStatisticNumericSummary Finish()
    {
        var digits = BigInteger.Abs(_scaled).ToString(CultureInfo.InvariantCulture).PadLeft(29, '0');
        var literal = (digits[..^28] + "." + digits[^28..]).TrimEnd('0').TrimEnd('.');
        return new((_scaled.Sign < 0 ? "-" : "") + literal, Count);
    }
}

internal sealed class NativeStatisticTextChunks
{
    private readonly NativeStatisticCalculationBudget _budget;
    private readonly List<NativeStatisticTextChunk> _chunks = new();
    private readonly StringBuilder _buffer = new();
    private int _bytes;
    internal NativeStatisticTextChunks(NativeStatisticCalculationBudget budget) => _budget = budget;
    internal void Append(string text)
    {
        _ = NativeStatisticText.Utf8Bytes(text); // Reject invalid Unicode; never replace it.
        Span<char> utf16 = stackalloc char[2];
        var visited = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if ((visited++ & 4095) == 0) _budget.CheckCancellation();
            if ((long)_bytes + rune.Utf8SequenceLength > _budget.Limits.ChunkBytes) Flush();
            var length = rune.EncodeToUtf16(utf16);
            _buffer.Append(utf16[..length]);
            _bytes += rune.Utf8SequenceLength;
        }
    }
    private void Flush()
    {
        if (_buffer.Length == 0) return;
        _budget.Retain(64L + _bytes + 2L * _buffer.Length);
        _chunks.Add(new(_chunks.Count, _buffer.ToString()));
        _buffer.Clear(); _bytes = 0;
    }
    internal IReadOnlyList<NativeStatisticTextChunk> Finish() { Flush(); return _chunks.ToArray(); }
}
