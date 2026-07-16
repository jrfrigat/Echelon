using System.Text.RegularExpressions;

namespace ReleaseOrchestrator.Core.Parsing;

/// <summary>
/// Extracts a tracker issue key from a branch name (e.g. <c>feature/PROJ-123-thing</c>).
/// Lives in Core because both the webhook ingress and the VCS sync must agree: two
/// copies of this rule previously disagreed, so the same MR linked to a different task
/// depending on whether it arrived by webhook or by sync.
/// </summary>
public static class BranchTaskParser
{
    // Tracker keys are uppercase, may contain digits after the first letter (S3-42), and
    // may be a single letter (X-1). Anchored to a boundary so release/2024-01-ABC-15 does
    // not yield "01-ABC"; the leading (?<![A-Z0-9]) stops a match mid-token.
    private static readonly Regex Pattern = new(
        @"(?<![A-Za-z0-9])([A-Z][A-Z0-9]*-\d+)(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <returns>The first issue key found, or <c>null</c> — including for null/blank input.</returns>
    public static string? ParseTaskId(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName)) return null;

        var match = Pattern.Match(branchName);
        return match.Success ? match.Groups[1].Value : null;
    }
}
