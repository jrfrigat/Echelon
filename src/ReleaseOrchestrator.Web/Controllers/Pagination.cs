namespace ReleaseOrchestrator.Web.Controllers;

/// <summary>
/// Clamps paging input at the boundary. Unbounded pageSize let any authenticated caller
/// pull an entire table into memory, and page=0 produced Skip(-50), which throws.
/// </summary>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page.</param>
public readonly record struct Paging(int Page, int PageSize)
{
    /// <summary>The largest page a caller may ask for, however large a number they send.</summary>
    public const int MaxPageSize = 200;

    /// <summary>The page size used when a caller names none.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Clamps caller-supplied paging into the safe range. The only way to construct one.</summary>
    /// <param name="page">The requested page; anything below 1 becomes 1.</param>
    /// <param name="pageSize">The requested size; clamped to 1..<see cref="MaxPageSize"/>.</param>
    public static Paging From(int page, int pageSize) => new(
        Math.Max(page, 1),
        Math.Clamp(pageSize, 1, MaxPageSize));

    /// <summary>Rows to skip for this page. Never negative, because <see cref="Page"/> is never below 1.</summary>
    public int Skip => (Page - 1) * PageSize;
}
