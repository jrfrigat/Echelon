namespace ReleaseOrchestrator.Core.Parsing;

/// <summary>
/// Normalizes VCS labels into the one form everything else compares against.
/// </summary>
/// <remarks>
/// Labels are typed by people in a web UI, so <c>Ready-For-Prod</c>, <c>ready-for-prod </c> and
/// <c>ready-for-prod</c> are the same intent and must not be three different answers to "may this
/// deploy to production". Normalizing once, here, is what keeps the comparison from being spelled
/// slightly differently at each call site — the failure this codebase has already recorded once for
/// merge-request status.
/// </remarks>
public static class LabelSet
{
    /// <summary>
    /// Trims, lower-cases, drops blanks and duplicates, and sorts.
    /// </summary>
    /// <param name="labels">Labels as the provider reported them.</param>
    /// <returns>The canonical set; empty when there is nothing usable.</returns>
    /// <remarks>
    /// Sorted so that two observations of the same set produce the same sequence, which is what makes
    /// <see cref="Canonical"/> a stable change-detection key. Ordinal comparison throughout: a label
    /// is an identifier, and culture-aware casing turns Turkish "I" into a different label.
    /// </remarks>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? labels)
    {
        if (labels is null) return [];

        return [.. labels
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!.Trim().ToLowerInvariant())
            // The canonical form joins on ',', and the readiness gate splits on it, so a label that
            // itself contains a comma would be torn into separate tokens on the way back out and could
            // match a rule label it never carried -- a false admit into a gated environment. No
            // supported provider allows a comma in a label name, so such a value is malformed input;
            // drop it rather than let it corrupt the set it joins into.
            .Where(l => !l.Contains(',', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The normalized set as one string, for detecting that a label set actually changed.
    /// </summary>
    /// <param name="labels">Labels as the provider reported them.</param>
    /// <remarks>
    /// Compared against the previously stored value so a repeat observation of an unchanged set costs
    /// no journal row and no replan. That comparison — rather than a delivery-id inbox — is what makes
    /// "approved, revoked, approved again" work: a set returning to an earlier value is a real change
    /// and must not be mistaken for a duplicate delivery.
    /// </remarks>
    public static string Canonical(IEnumerable<string?>? labels) => string.Join(',', Normalize(labels));
}
