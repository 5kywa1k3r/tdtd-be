namespace tdtd_be.DTOs.Common
{
    public sealed record PagedResult<T>(
        IReadOnlyList<T> Rows,
        long TotalRows,
        int Page,
        int PageSize
    );
}
