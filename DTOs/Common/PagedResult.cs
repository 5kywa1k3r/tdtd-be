using tdtd_be.DTOs.Works;

namespace tdtd_be.DTOs.Common
{
    public sealed class PagedResult<T>
    {
        public PagedResult(List<T> rows, long total, int page, int pageSize)
        {
            Rows = rows;
            TotalRows = total;
            Page = page;
            PageSize = pageSize;
        }

        public List<T> Rows { get; set; } = new();
        public long TotalRows { get; set; }
        public int Page { get; set; }         // 0-based
        public int PageSize { get; set; }
    }
}
