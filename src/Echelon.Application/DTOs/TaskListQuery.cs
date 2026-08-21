namespace Echelon.Application.DTOs;

/// <summary>
/// Which slice of the task list to read: the page, and the column filters narrowing it.
/// </summary>
/// <remarks>
/// One object for both the page and the count, because the two must be asked the same question. A
/// filtered page beside an unfiltered total is a list that says "3 of 500" and offers pages that come
/// back empty - the count has to be of the same rows.
/// </remarks>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="Key">Substring of the task key, case-insensitive; null for no filter.</param>
/// <param name="Title">Substring of the title, case-insensitive; null for no filter.</param>
/// <param name="Status">Substring of the tracker status, case-insensitive; null for no filter.</param>
public sealed record TaskListQuery(
    int Page,
    int PageSize,
    string? Key = null,
    string? Title = null,
    string? Status = null);
