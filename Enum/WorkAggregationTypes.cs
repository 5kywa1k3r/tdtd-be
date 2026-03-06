namespace tdtd_be.Enum;

public static class WorkAggregationTypes
{
    // cộng gộp theo ma trận / giữ cấu trúc bảng
    public const string Matrix = "MATRIX";

    // mỗi đơn vị thành 1 dòng
    public const string UnitRowCol = "UNIT_ROW_COL";

    public static readonly string[] All =
    {
        Matrix,
        UnitRowCol
    };
}