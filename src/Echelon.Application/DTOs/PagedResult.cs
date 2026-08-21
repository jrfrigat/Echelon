namespace Echelon.Application.DTOs;

/// <summary>
/// One page of a list, and how much of the list it is.
/// </summary>
/// <remarks>
/// The shape every paged endpoint answers with, declared once. It used to be an anonymous object in
/// each controller and a hand-written record in the browser - seven copies of four fields, none of
/// which the compiler compared. A grid asks for a page, so it needs the total to size its pager: the
/// count must be of the rows the same query selected, or the pager offers pages that come back empty.
/// </remarks>
/// <typeparam name="T">The row type.</typeparam>
/// <param name="Total">Rows the query matches, not rows on this page.</param>
/// <param name="Page">1-based page number, as clamped by the server.</param>
/// <param name="PageSize">Rows per page, as clamped by the server.</param>
/// <param name="Items">This page's rows.</param>
public record PagedResult<T>(int Total, int Page, int PageSize, IReadOnlyList<T> Items);
