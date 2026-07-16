using System.Text.RegularExpressions;

namespace ReleaseOrchestrator.Providers.GitLab;

/// <summary>
/// Extracts a tracker issue key from a branch name (e.g. <c>feature/PROJ-123-thing</c>).
/// </summary>
/// <remarks>
/// <para>
/// A dialect, not a universal rule: <c>[A-Z][A-Z0-9]*-\d+</c> is the Jira and Yandex.Tracker key
/// shape, whereas GitHub Issues would want <c>#123</c>. It sits in an adapter rather than the
/// domain for that reason, and is reached through <c>IVcsProvider.ParseTaskKeyFromBranch</c> so
/// that callers get an answer without learning whose dialect produced it.
/// </para>
/// <para>
/// One copy, deliberately. Two copies of this rule once disagreed — one did not understand
/// <c>S3-42</c>, the other did not understand <c>X-1</c> — so the same merge request linked to a
/// different task depending on whether it arrived by webhook or by sync.
/// </para>
/// </remarks>
public static partial class GitLabBranchTaskParser
{
    // Tracker keys are uppercase, may contain digits after the first letter (S3-42), and
    // may be a single letter (X-1). Anchored to a boundary so release/2024-01-ABC-15 does
    // not yield "01-ABC"; the leading (?<![A-Za-z0-9]) stops a match mid-token.
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])([A-Z][A-Z0-9]*-\d+)(?![A-Za-z0-9])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex TaskKeyPattern();

    /// <summary>Finds the issue key a branch names.</summary>
    /// <param name="branchName">The branch name; may be null or blank.</param>
    /// <returns>The first issue key found, or <c>null</c> — including for null or blank input.</returns>
    public static string? ParseTaskId(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName)) return null;

        var match = TaskKeyPattern().Match(branchName);
        return match.Success ? match.Groups[1].Value : null;
    }
}
