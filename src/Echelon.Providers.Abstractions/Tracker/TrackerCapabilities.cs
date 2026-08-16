namespace Echelon.Providers.Abstractions.Tracker;

/// <summary>
/// What one tracker connection can actually do.
/// </summary>
/// <remarks>
/// Per connection, for the same reason as <see cref="Vcs.VcsCapabilities"/>: a self-hosted tracker
/// is whatever version its owner runs.
/// </remarks>
public sealed record TrackerCapabilities
{
    /// <summary>The server version reported at connect time, or <c>null</c> when it was not detected.</summary>
    public string? ServerVersion { get; init; }

    /// <summary>Capabilities of a provider that has told us nothing: assume the minimum.</summary>
    public static TrackerCapabilities None { get; } = new();
}
