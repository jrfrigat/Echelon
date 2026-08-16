using Echelon.Core.Enums;

namespace Echelon.Core.Parsing;

/// <summary>
/// The normalized readiness signals a merge request carries, and the readiness a rule is written
/// against. A signal is a <c>kind:value</c> token - <c>label:ready-for-prod</c>, <c>mr-status:merged</c>,
/// <c>pipeline:success</c> - so one rule language spans every way a project decides a merge request is
/// ready, rather than readiness being a single hardcoded label.
/// </summary>
/// <remarks>
/// Pure and in Core with the other readiness rules. Signals are compared as a set by the same
/// resolver the label gate used (<see cref="ReadinessResolver"/>), which is why this only has to
/// produce the tokens: a merge request's set from what it carries, and a rule's required set from
/// what an operator configured, both canonical so the comparison is a byte match.
/// </remarks>
public static class ReadinessSignals
{
    /// <summary>Prefix for a label signal: the merge request carries this label.</summary>
    public const string LabelPrefix = "label:";

    /// <summary>Prefix for a status signal: the merge request is in this normalized status.</summary>
    public const string StatusPrefix = "mr-status:";

    /// <summary>Prefix for a pipeline signal: the merge request's latest pipeline had this result.</summary>
    public const string PipelinePrefix = "pipeline:";

    /// <summary>Builds a label signal token from a label value.</summary>
    /// <param name="label">The label; normalized (trimmed, lower-cased) into the token.</param>
    public static string Label(string label) => LabelPrefix + Normalize(label);

    /// <summary>Builds a status signal token from a merge-request status.</summary>
    /// <param name="status">The normalized status.</param>
    public static string Status(MergeRequestStatus status) => StatusPrefix + status.ToString().ToLowerInvariant();

    /// <summary>Builds a pipeline signal token from a pipeline result string.</summary>
    /// <param name="result">The provider's pipeline result, e.g. <c>success</c>; normalized into the token.</param>
    public static string Pipeline(string result) => PipelinePrefix + Normalize(result);

    /// <summary>
    /// The full set of signals a merge request currently carries: one per label, one for its status,
    /// and one for its pipeline result when known.
    /// </summary>
    /// <param name="labels">The merge request's labels, in any form.</param>
    /// <param name="status">The merge request's normalized status.</param>
    /// <param name="pipelineResult">The latest pipeline result, or null when none is known.</param>
    /// <returns>The canonical signal set - trimmed, lower-cased, de-duplicated, sorted.</returns>
    public static IReadOnlyList<string> For(
        IEnumerable<string?>? labels,
        MergeRequestStatus status,
        string? pipelineResult = null)
    {
        var signals = new List<string> { Status(status) };

        if (labels is not null)
            signals.AddRange(labels
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => Label(l!)));

        if (!string.IsNullOrWhiteSpace(pipelineResult))
            signals.Add(Pipeline(pipelineResult));

        // Same canonical form as a rule's required set: a signal that contains the delimiter would tear
        // in two on the way through the CSV, so drop it, then de-dup and sort for a stable comparison.
        return [.. signals
            .Where(s => !s.Contains(',', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
