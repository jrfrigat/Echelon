using Flare.Components;

namespace Echelon.Pwa.Shared;

/// <summary>
/// How every server-paged grid in the app is set up, and how a column filter box reaches the API.
/// </summary>
/// <remarks>
/// One place, because these lists are the same thing wearing different columns: a page of rows the
/// server chose. Each page repeating its own page sizes is how they drift apart, and how one of them
/// ends up offering a size the API refuses.
/// </remarks>
public static class GridDefaults
{
    /// <summary>Rows per page a user can choose. The largest is the API's own ceiling (Paging.MaxPageSize).</summary>
    public static readonly IReadOnlyList<int> RowsPerPage = [25, 50, 100, 200];

    /// <summary>The size a grid opens at.</summary>
    public const int PageSize = 25;

    /// <summary>The value in a column's filter box, or null when the box is empty.</summary>
    /// <param name="request">The request the grid handed the provider.</param>
    /// <param name="columnId">The column's <c>Id</c> - never its title, which is translated.</param>
    /// <remarks>
    /// Blank is not a filter: an empty box has to mean "everything", or clearing it would search for
    /// the empty string and the list would come back empty with nothing on screen to explain it.
    /// </remarks>
    public static string? Filter(this DataGridRequest request, string columnId)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Filters is not null
               && request.Filters.TryGetValue(columnId, out var value)
               && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    /// <summary>The 1-based page number the API expects; the grid counts from zero.</summary>
    /// <param name="request">The request the grid handed the provider.</param>
    public static int ApiPage(this DataGridRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Page + 1;
    }
}
