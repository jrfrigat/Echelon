using System.Text.RegularExpressions;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Core.Parsing;

/// <summary>
/// Reads a task key out of a merge request, from the source and pattern a connection configures.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and in Core with the other parsing rules, for the same reason as <see cref="LabelSet"/>: the
/// rule that decides which task a merge request belongs to must be exercisable without a database, and
/// it must be the single copy. Two copies of the old branch parser once disagreed — one did not
/// understand <c>S3-42</c>, the other <c>X-1</c> — so the same merge request linked to different tasks
/// by which path imported it. This replaces that provider-owned dialect with one configurable rule.
/// </para>
/// <para>
/// The pattern is a regex the operator sets; its first capture group is the key, or the whole match
/// when it has no group. An unparsable or non-matching pattern links nothing rather than throwing —
/// the pattern is validated when the connection is saved, so this guards only the hot path.
/// </para>
/// </remarks>
public static class TaskKeyExtractor
{
    // A pathological pattern must not hang ingestion; matching is bounded, as the old parser's was.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Extracts the task key a merge request names, per the configured source and pattern.</summary>
    /// <param name="source">Which field to read.</param>
    /// <param name="pattern">The regex to apply; first capture group, or whole match, is the key.</param>
    /// <param name="branch">The source branch name.</param>
    /// <param name="title">The merge request title.</param>
    /// <param name="labels">The merge request's labels.</param>
    /// <returns>The key, or <c>null</c> when the source is empty, the pattern is invalid, or nothing matched.</returns>
    public static string? Extract(
        TaskKeySource source,
        string? pattern,
        string? branch,
        string? title,
        IEnumerable<string>? labels)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return source switch
        {
            TaskKeySource.Branch => FirstMatch(regex, branch),
            TaskKeySource.Title => FirstMatch(regex, title),
            TaskKeySource.Label => labels?
                .Select(label => FirstMatch(regex, label))
                .FirstOrDefault(key => key is not null),
            _ => null
        };
    }

    private static string? FirstMatch(Regex regex, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        Match match;
        try
        {
            match = regex.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!match.Success) return null;

        // The first capture group is the key when the pattern defines one (the default pattern
        // brackets the key so surrounding boundary assertions do not become part of it); otherwise the
        // whole match is the key.
        return match.Groups.Count > 1 && match.Groups[1].Success
            ? match.Groups[1].Value
            : match.Value;
    }
}
