namespace ReleaseOrchestrator.Infrastructure.Persistence;

/// <summary>Which relational database the two contexts run against.</summary>
/// <remarks>
/// Unlike the coordination backend, this one is a source of truth — so the choice is only about
/// which database an operator already runs, never about doing without. Both providers get the same
/// model; what differs is the handful of things the two databases genuinely do not share, and each
/// of those is named in <see cref="ProviderSpecificMapping"/> rather than left to a convention.
/// </remarks>
public class DatabaseOptions
{
    /// <summary>Configuration section carrying these settings.</summary>
    public const string SectionName = "Database";

    /// <summary>Which provider to use. Matched in canonical form, so <c>PostgreSQL</c> works.</summary>
    public string Provider { get; set; } = DatabaseProviders.SqlServer;
}

/// <summary>The databases this build has migrations for.</summary>
public static class DatabaseProviders
{
    /// <summary>Microsoft SQL Server 2019+.</summary>
    public const string SqlServer = "sqlserver";

    /// <summary>PostgreSQL 13+.</summary>
    public const string PostgreSql = "postgresql";

    /// <summary>Every provider this build registers, for validation and error messages.</summary>
    public static readonly IReadOnlyList<string> All = [SqlServer, PostgreSql];

    /// <summary>The migrations assembly that owns each provider's schema history.</summary>
    /// <remarks>
    /// Separate assemblies rather than one with both: a migration is generated SQL, not a
    /// description of intent, so "nvarchar(200)" and "character varying(200)" cannot share a file.
    /// EF picks the assembly by provider at startup.
    /// </remarks>
    public static string MigrationsAssembly(string provider) => provider switch
    {
        SqlServer => "ReleaseOrchestrator.Migrations.MsSql",
        PostgreSql => "ReleaseOrchestrator.Migrations.Postgres",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "No migrations assembly for this provider.")
    };
}
