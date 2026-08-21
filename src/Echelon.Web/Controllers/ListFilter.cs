using Echelon.Infrastructure.Persistence.Models;

namespace Echelon.Web.Controllers;

/// <summary>
/// Turns a grid's column filter boxes into a narrowed query.
/// </summary>
/// <remarks>
/// <para>
/// The filters run in the database, not in the browser, because the browser holds one page: a filter
/// applied there searches the slice and presents the result as though it had searched the list - the
/// same trap that keeps these columns from being client-sortable.
/// </para>
/// <para>
/// Both sides are lowercased instead of relying on the database's collation. SQL Server compares
/// case-insensitively by default and PostgreSQL does not, so the same text would match on one
/// provider and not the other, and nothing in the UI would explain why. It costs the index on these
/// columns, which is acceptable: these are configuration tables, read by one operator at a time.
/// </para>
/// <para>
/// One overload per entity rather than a generic over an interface: Entity Framework translates a
/// property access on a known entity type, and quietly stops translating - falling back to loading
/// the table and filtering in memory - when the expression is written against something else.
/// </para>
/// </remarks>
internal static class ListFilter
{
    /// <summary>A filter box's text, lowercased, or null when it selects everything.</summary>
    /// <param name="value">Whatever the box holds, including blanks.</param>
    public static string? Needle(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>Narrows VCS connections by the columns the admin grid shows.</summary>
    /// <param name="connections">The unfiltered set.</param>
    /// <param name="name">Substring of the connection name.</param>
    /// <param name="type">Substring of the provider type.</param>
    /// <param name="apiUrl">Substring of the API URL.</param>
    public static IQueryable<VcsConnection> Apply(
        IQueryable<VcsConnection> connections, string? name, string? type, string? apiUrl)
    {
        if (Needle(name) is { } n) connections = connections.Where(c => c.Name.ToLower().Contains(n));
        if (Needle(type) is { } t) connections = connections.Where(c => c.ProviderType.ToLower().Contains(t));
        if (Needle(apiUrl) is { } u) connections = connections.Where(c => c.ApiUrl.ToLower().Contains(u));

        return connections;
    }

    /// <summary>Narrows tracker connections by the columns the admin grid shows.</summary>
    /// <param name="connections">The unfiltered set.</param>
    /// <param name="name">Substring of the connection name.</param>
    /// <param name="type">Substring of the provider type.</param>
    /// <param name="apiUrl">Substring of the API URL.</param>
    public static IQueryable<TrackerConnection> Apply(
        IQueryable<TrackerConnection> connections, string? name, string? type, string? apiUrl)
    {
        if (Needle(name) is { } n) connections = connections.Where(c => c.Name.ToLower().Contains(n));
        if (Needle(type) is { } t) connections = connections.Where(c => c.ProviderType.ToLower().Contains(t));
        if (Needle(apiUrl) is { } u) connections = connections.Where(c => c.ApiUrl.ToLower().Contains(u));

        return connections;
    }

    /// <summary>Narrows repositories by the columns the admin grid shows.</summary>
    /// <param name="repositories">The unfiltered set.</param>
    /// <param name="name">Substring of the repository name.</param>
    /// <param name="externalId">Substring of the provider-side id (a path, for GitLab).</param>
    /// <param name="connection">Substring of the owning VCS connection's name.</param>
    public static IQueryable<Repository> Apply(
        IQueryable<Repository> repositories, string? name, string? externalId, string? connection)
    {
        if (Needle(name) is { } n) repositories = repositories.Where(r => r.Name.ToLower().Contains(n));
        if (Needle(externalId) is { } e) repositories = repositories.Where(r => r.ExternalId.ToLower().Contains(e));
        if (Needle(connection) is { } c) repositories = repositories.Where(r => r.Connection.Name.ToLower().Contains(c));

        return repositories;
    }
}
