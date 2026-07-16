using System.Globalization;

namespace ReleaseOrchestrator.Providers.GitLab;

/// <summary>
/// A GitLab server version, comparable.
/// </summary>
/// <remarks>
/// GitLab reports things like <c>17.5.1</c>, <c>16.11.0-ee</c> or <c>15.0.0-pre</c>. Only the
/// numeric head orders releases; the suffix says which edition or build it is, and comparing it
/// as text would put <c>16.11</c> before <c>16.9</c>. Renovate gates features the same way
/// (<c>semver.lt(version, '13.9.0')</c>) because a self-hosted install is whatever version its
/// owner last upgraded to.
/// </remarks>
public readonly record struct GitLabServerVersion : IComparable<GitLabServerVersion>
{
    private GitLabServerVersion(int major, int minor, int patch, string raw)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Raw = raw;
    }

    /// <summary>Major version component.</summary>
    public int Major { get; }

    /// <summary>Minor version component.</summary>
    public int Minor { get; }

    /// <summary>Patch version component; zero when the server did not report one.</summary>
    public int Patch { get; }

    /// <summary>The string the server reported, kept verbatim for logs and diagnostics.</summary>
    public string Raw { get; }

    /// <summary>Parses a version string, ignoring any edition or pre-release suffix.</summary>
    /// <param name="value">The raw value from GitLab's version endpoint.</param>
    /// <param name="version">The parsed version when parsing succeeds.</param>
    /// <returns><c>false</c> when the value is blank or has no numeric head.</returns>
    public static bool TryParse(string? value, out GitLabServerVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();

        // Cut the suffix at the first character that cannot start a numeric component. "-ee",
        // "+ce.0" and "rc1" all order identically for our purposes: they do not.
        var head = trimmed.AsSpan();
        var end = 0;
        while (end < head.Length && (char.IsAsciiDigit(head[end]) || head[end] == '.')) end++;
        head = head[..end];

        if (head.IsEmpty) return false;

        var parts = head.ToString().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)) return false;

        var minor = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor))
            return false;

        var patch = 0;
        if (parts.Length > 2 && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch))
            return false;

        version = new GitLabServerVersion(major, minor, patch, trimmed);
        return true;
    }

    /// <summary>Orders by major, then minor, then patch.</summary>
    /// <param name="other">The version to compare against.</param>
    /// <returns>Negative, zero or positive per <see cref="IComparable{T}"/>.</returns>
    public int CompareTo(GitLabServerVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    /// <summary>Whether this version is at least <paramref name="major"/>.<paramref name="minor"/>.</summary>
    /// <param name="major">Required major component.</param>
    /// <param name="minor">Required minor component.</param>
    /// <returns><c>true</c> when this version is the same or newer.</returns>
    public bool IsAtLeast(int major, int minor) =>
        CompareTo(new GitLabServerVersion(major, minor, 0, string.Empty)) >= 0;

    /// <summary>Whether one version precedes another.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns><c>true</c> when <paramref name="left"/> is older.</returns>
    public static bool operator <(GitLabServerVersion left, GitLabServerVersion right) => left.CompareTo(right) < 0;

    /// <summary>Whether one version follows another.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns><c>true</c> when <paramref name="left"/> is newer.</returns>
    public static bool operator >(GitLabServerVersion left, GitLabServerVersion right) => left.CompareTo(right) > 0;

    /// <summary>Whether one version precedes or equals another.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns><c>true</c> when <paramref name="left"/> is the same or older.</returns>
    public static bool operator <=(GitLabServerVersion left, GitLabServerVersion right) => left.CompareTo(right) <= 0;

    /// <summary>Whether one version follows or equals another.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns><c>true</c> when <paramref name="left"/> is the same or newer.</returns>
    public static bool operator >=(GitLabServerVersion left, GitLabServerVersion right) => left.CompareTo(right) >= 0;

    /// <summary>The raw string the server reported.</summary>
    /// <returns><see cref="Raw"/>.</returns>
    public override string ToString() => Raw;
}
