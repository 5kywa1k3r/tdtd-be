using tdtd_be.Enum;

namespace tdtd_be.Services.WorkAssignments.Aggregate;

internal static class ReportAggregateHelper
{
    public static string ResolveAggregateMode(string templateRuntimeType)
    {
        return templateRuntimeType switch
        {
            "TABLE_HORIZONTAL" => ReportAggregateMode.ConcatRows,
            "TABLE_VERTICAL" => ReportAggregateMode.ConcatColumns,
            "MATRIX" => ReportAggregateMode.SumCells,
            _ => ReportAggregateMode.SumCells
        };
    }

    public static object? AggregateWorkbook(
        string templateRuntimeType,
        IEnumerable<object?> workbooks)
    {
        var list = workbooks.Where(x => x != null).ToList();
        if (list.Count == 0) return null;
        if (list.Count == 1) return list[0];

        var mode = ResolveAggregateMode(templateRuntimeType);
        return mode switch
        {
            ReportAggregateMode.ConcatRows => AggregateConcatRows(list),
            ReportAggregateMode.ConcatColumns => AggregateConcatColumns(list),
            _ => AggregateSumCells(list)
        };
    }

    public static object? AggregateSumCells(List<object?> workbooks)
    {
        var acc = ReportAggregateValueHelper.ToDictionary(workbooks[0]);

        for (var i = 1; i < workbooks.Count; i++)
        {
            var current = ReportAggregateValueHelper.ToDictionary(workbooks[i]);
            acc = SumDictionary(acc, current);
        }

        return acc;
    }

    public static object? AggregateConcatRows(List<object?> workbooks)
    {
        var rows = new List<object?>();

        foreach (var workbook in workbooks)
        {
            var dict = ReportAggregateValueHelper.ToDictionary(workbook);

            if (dict.TryGetValue("rows", out var rowObj))
            {
                rows.AddRange(ReportAggregateValueHelper.ToList(rowObj));
            }
            else
            {
                rows.Add(dict);
            }
        }

        return new Dictionary<string, object?>
        {
            ["rows"] = rows
        };
    }

    public static object? AggregateConcatColumns(List<object?> workbooks)
    {
        var cols = new List<object?>();

        foreach (var workbook in workbooks)
        {
            var dict = ReportAggregateValueHelper.ToDictionary(workbook);

            if (dict.TryGetValue("cols", out var colObj))
            {
                cols.AddRange(ReportAggregateValueHelper.ToList(colObj));
            }
            else if (dict.TryGetValue("columns", out var columnsObj))
            {
                cols.AddRange(ReportAggregateValueHelper.ToList(columnsObj));
            }
            else
            {
                cols.Add(dict);
            }
        }

        return new Dictionary<string, object?>
        {
            ["cols"] = cols
        };
    }

    private static Dictionary<string, object?> SumDictionary(
        Dictionary<string, object?> left,
        Dictionary<string, object?> right)
    {
        var result = new Dictionary<string, object?>(left, StringComparer.OrdinalIgnoreCase);

        foreach (var kv in right)
        {
            if (!result.TryGetValue(kv.Key, out var leftValue))
            {
                result[kv.Key] = kv.Value;
                continue;
            }

            result[kv.Key] = SumValue(leftValue, kv.Value);
        }

        return result;
    }

    private static object? SumValue(object? left, object? right)
    {
        if (left == null) return right;
        if (right == null) return left;

        if (ReportAggregateValueHelper.TryToDecimal(left, out var d1) &&
            ReportAggregateValueHelper.TryToDecimal(right, out var d2))
        {
            return ReportAggregateValueHelper.NormalizeNumber(d1 + d2);
        }

        var leftDict = ReportAggregateValueHelper.ToDictionary(left);
        var rightDict = ReportAggregateValueHelper.ToDictionary(right);
        if (leftDict.Count > 0 && rightDict.Count > 0)
            return SumDictionary(leftDict, rightDict);

        var leftList = ReportAggregateValueHelper.ToList(left);
        var rightList = ReportAggregateValueHelper.ToList(right);
        if (leftList.Count > 0 || rightList.Count > 0)
        {
            var max = Math.Max(leftList.Count, rightList.Count);
            var result = new List<object?>(max);

            for (int i = 0; i < max; i++)
            {
                var l = i < leftList.Count ? leftList[i] : null;
                var r = i < rightList.Count ? rightList[i] : null;
                result.Add(SumValue(l, r));
            }

            return result;
        }

        return left;
    }
}