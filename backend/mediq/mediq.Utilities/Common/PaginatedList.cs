namespace mediq.Utilities.Common;

public class PaginatedList<T>
{
    public IReadOnlyList<T> List { get; }
    public int PageNumber { get; }
    public int PageCount { get; }
    public int TotalCount { get; }
    private int PageSize { get; }

    private PaginatedList(IReadOnlyList<T> items, int totalCount, int pageNumber, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (totalCount < 0) throw new ArgumentOutOfRangeException(nameof(totalCount));

        List = items;
        TotalCount = totalCount;
        PageNumber = pageNumber;
        PageSize = pageSize;
        PageCount = (int)Math.Ceiling(totalCount / (double)pageSize);
    }

    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < PageCount;

    public static async Task<PaginatedList<T>> CreateAsync(
        IQueryable<T> source,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var count = await source.CountAsync(cancellationToken);
        var items = await source
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedList<T>(items, count, pageNumber, pageSize);
    }

    /// <summary>
    /// Paginates an already-materialised in-memory list. Use when the page must be
    /// computed in memory (aggregation/joins that can't run in SQL) but the response
    /// should still carry pagination metadata.
    /// </summary>
    public static PaginatedList<T> Create(IReadOnlyList<T> source, int pageNumber, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        var items = source
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return new PaginatedList<T>(items, source.Count, pageNumber, pageSize);
    }

    /// <summary>
    /// Projects each item to a new shape while preserving pagination metadata.
    /// Use when a page must be paginated in SQL (on the raw entity) but the
    /// response DTO needs in-memory work (JSON parsing, joins) that can't run in the query.
    /// </summary>
    public PaginatedList<TResult> Map<TResult>(Func<T, TResult> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var mapped = List.Select(selector).ToList();
        return new PaginatedList<TResult>(mapped, TotalCount, PageNumber, PageSize);
    }
}
