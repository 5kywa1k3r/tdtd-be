using System.Text.Json;

namespace tdtd_be.Services;

public static class DynamicExcelRuntimePolicy
{
    public const int MaxBackgroundTableStatisticInputCells = 250;
    public const int MaxDirectTableAggregateInputCells = 10000;
    public const int MaxTableStatisticInputCells = MaxBackgroundTableStatisticInputCells;

    public static bool ShouldDisableBackgroundTableStatistics(int inputCellCount)
        => inputCellCount > MaxBackgroundTableStatisticInputCells;

    public static bool ShouldDisableTableStatistics(int inputCellCount)
        => ShouldDisableBackgroundTableStatistics(inputCellCount);

    public static bool CanRunDirectTableAggregation(int inputCellCount)
        => inputCellCount <= MaxDirectTableAggregateInputCells;

    public static string BuildBackgroundTableStatisticsDisabledReason(int inputCellCount)
        => $"Bang co {inputCellCount} o nhap, vuot nguong {MaxBackgroundTableStatisticInputCells}; he thong chi luu du lieu va khong thong ke tung o trong bang.";

    public static string BuildTableStatisticsDisabledReason(int inputCellCount)
        => BuildBackgroundTableStatisticsDisabledReason(inputCellCount);

    public static string BuildDirectTableAggregationLimitReason(int inputCellCount)
        => $"Bang co {inputCellCount} o nhap, vuot nguong tong hop truc tiep {MaxDirectTableAggregateInputCells}; he thong bo qua bang nay khi tinh thong ke co ban.";

    public static int CountInputCells(
        DynamicExcelRuntimeRect dataRect,
        IReadOnlyCollection<DynamicExcelRuntimeRect>? specialRanges = null)
    {
        if (dataRect.R1 < dataRect.R0 || dataRect.C1 < dataRect.C0)
            return 0;

        var count = 0;
        for (var r = dataRect.R0; r <= dataRect.R1; r++)
        {
            for (var c = dataRect.C0; c <= dataRect.C1; c++)
            {
                if (specialRanges?.Any(range => Contains(range, r, c)) == true)
                    continue;

                count++;
            }
        }

        return count;
    }

    public static IReadOnlyList<DynamicExcelRuntimeRect> ReadSpecialRanges(
        JsonElement owner,
        DynamicExcelRuntimeRect dataRect)
    {
        var ranges = new List<DynamicExcelRuntimeRect>();
        if (owner.ValueKind != JsonValueKind.Object ||
            !TryGetJsonProperty(owner, "specialRanges", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return ranges;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var role = NormalizeSpecialRole(ReadJsonString(item, "role") ?? ReadJsonString(item, "kind") ?? ReadJsonString(item, "type"));
            if (string.IsNullOrWhiteSpace(role))
                continue;

            var r0 = ReadJsonInt(item, "r0") ?? ReadJsonInt(item, "R0");
            var c0 = ReadJsonInt(item, "c0") ?? ReadJsonInt(item, "C0");
            var r1 = ReadJsonInt(item, "r1") ?? ReadJsonInt(item, "R1");
            var c1 = ReadJsonInt(item, "c1") ?? ReadJsonInt(item, "C1");
            if (!r0.HasValue || !c0.HasValue || !r1.HasValue || !c1.HasValue)
                continue;

            var range = new DynamicExcelRuntimeRect(r0.Value, c0.Value, r1.Value, c1.Value);
            if (range.R1 < range.R0 || range.C1 < range.C0)
                continue;
            if (!Contains(dataRect, range))
                continue;
            if (ranges.Any(existing => Overlaps(existing, range)))
                continue;

            ranges.Add(range);
        }

        return ranges;
    }

    public static bool Contains(DynamicExcelRuntimeRect rect, int r, int c)
        => r >= rect.R0 && r <= rect.R1 && c >= rect.C0 && c <= rect.C1;

    public static bool Contains(DynamicExcelRuntimeRect outer, DynamicExcelRuntimeRect inner)
        => inner.R0 >= outer.R0 && inner.C0 >= outer.C0 && inner.R1 <= outer.R1 && inner.C1 <= outer.C1;

    public static bool Overlaps(DynamicExcelRuntimeRect a, DynamicExcelRuntimeRect b)
        => a.R0 <= b.R1 && a.R1 >= b.R0 && a.C0 <= b.C1 && a.C1 >= b.C0;

    private static bool TryGetJsonProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static int? ReadJsonInt(JsonElement element, string name)
    {
        if (!TryGetJsonProperty(element, name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadJsonString(JsonElement element, string name)
        => TryGetJsonProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NormalizeSpecialRole(string? value)
    {
        var role = value?.Trim().ToUpperInvariant();
        if (role == "FORMULAR")
            role = "FORMULA";
        if (role == "HEADER")
            role = "TITLE";
        if (role is "STYLE" or "EMPTY" or "EMPTY_INPUT")
            role = "BLANK";
        return role is "FORMULA" or "TITLE" or "BLANK" ? role : null;
    }
}

public readonly record struct DynamicExcelRuntimeRect(int R0, int C0, int R1, int C1);
