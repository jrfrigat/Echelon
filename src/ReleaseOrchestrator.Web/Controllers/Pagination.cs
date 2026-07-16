namespace ReleaseOrchestrator.Web.Controllers;

/// <summary>
/// Clamps paging input at the boundary. Unbounded pageSize let any authenticated caller
/// pull an entire table into memory, and page=0 produced Skip(-50), which throws.
/// </summary>
public readonly record struct Paging(int Page, int PageSize)
{
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 50;

    public static Paging From(int page, int pageSize) => new(
        Math.Max(page, 1),
        Math.Clamp(pageSize, 1, MaxPageSize));

    public int Skip => (Page - 1) * PageSize;
}
