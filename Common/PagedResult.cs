namespace tdtd_be.Common
{
    public sealed class PagedResult<T>
    {
        public required IReadOnlyList<T> Items { get; init; }
        public required long Total { get; init; }
        public required int Page { get; init; }
        public required int PageSize { get; init; }
    }
}
