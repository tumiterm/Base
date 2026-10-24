namespace Base.Application.Common.Persistence;

/// <summary>A 1-based page request. Page size is clamped so a client cannot ask for the whole table.</summary>
public sealed record PageRequest
{
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 25;

    public PageRequest(int page = 1, int pageSize = DefaultPageSize)
    {
        Page = Math.Max(1, page);
        PageSize = Math.Clamp(pageSize, 1, MaxPageSize);
    }

    public int Page { get; }

    public int PageSize { get; }

    public int Skip => (Page - 1) * PageSize;
}

/// <summary>Where a page sits among the pages, whatever it holds (for pagers).</summary>
public interface IPagedResult
{
    int Page { get; }

    int TotalPages { get; }

    bool HasPrevious { get; }

    bool HasNext { get; }
}

/// <summary>One page of results plus the database-side total.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize) : IPagedResult
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < TotalPages;

    public static PagedResult<T> Empty(PageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new([], 0, request.Page, request.PageSize);
    }
}
