namespace Zladovce.Application.Common.Paging;

/// <summary>List response shape used by every paged endpoint (see API_SPECIFICATION.md, Conventions).</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalItems, int TotalPages)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public static PagedResult<T> Create(IReadOnlyList<T> items, int page, int pageSize, int totalItems)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxPageSize);
        ArgumentOutOfRangeException.ThrowIfNegative(totalItems);

        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)pageSize);
        return new PagedResult<T>(items, page, pageSize, totalItems, totalPages);
    }
}
