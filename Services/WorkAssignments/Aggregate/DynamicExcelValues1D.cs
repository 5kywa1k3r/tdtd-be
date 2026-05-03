namespace tdtd_be.Services.WorkAssignments.Aggregate;

internal static class DynamicExcelValues1D
{
    public static int GetExpectedLength(int dataRectR0, int dataRectC0, int dataRectR1, int dataRectC1)
        => GetDataRectRowCount(dataRectR0, dataRectR1) * GetDataRectColumnCount(dataRectC0, dataRectC1);

    public static List<decimal?> ExtractDataRectValues(
        IReadOnlyList<decimal?> values1D,
        int dataRectR0,
        int dataRectC0,
        int dataRectR1,
        int dataRectC1)
    {
        var rows = GetDataRectRowCount(dataRectR0, dataRectR1);
        var cols = GetDataRectColumnCount(dataRectC0, dataRectC1);
        var result = new List<decimal?>(rows * cols);

        for (var r = dataRectR0; r <= dataRectR1; r++)
        {
            for (var c = dataRectC0; c <= dataRectC1; c++)
            {
                // values1D is row-major inside dataRect, not absolute sheet coordinates.
                var index = (r - dataRectR0) * cols + (c - dataRectC0);
                result.Add(index >= 0 && index < values1D.Count ? values1D[index] : null);
            }
        }

        return result;
    }

    private static int GetDataRectRowCount(int r0, int r1)
        => Math.Max(0, r1 - r0 + 1);

    private static int GetDataRectColumnCount(int c0, int c1)
        => Math.Max(0, c1 - c0 + 1);
}
