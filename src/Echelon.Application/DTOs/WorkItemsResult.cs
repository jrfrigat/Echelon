namespace Echelon.Application.DTOs;

/// <summary>A page of work items, with a flag for when the scan cap bound.</summary>
/// <param name="Total">Rows the filters match, within the scan cap.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="Items">This page's rows.</param>
/// <param name="Truncated">
/// True when the scan cap bound before the filters ran, so this list is a slice of the work rather
/// than all of it. Said out loud, because a slice that looks complete is the failure worth avoiding.
/// </param>
public record WorkItemsResult(
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<WorkItemDto> Items,
    bool Truncated);
