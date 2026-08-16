using Echelon.Core.Enums;

namespace Echelon.Core.Parsing;

/// <summary>The outcome of a readiness decision, and where it came from.</summary>
/// <param name="IsReady">Whether the merge request may deploy to the environment.</param>
/// <param name="Source">What decided it - a person's pin, or the labels against the rule.</param>
public readonly record struct ReadinessDecision(bool IsReady, ReadinessSource Source);

/// <summary>
/// Decides whether a merge request may deploy to an environment: a person's pin if there is one,
/// otherwise its labels against the environment's rule.
/// </summary>
/// <remarks>
/// <para>
/// Pure, like <see cref="ReadinessResolver"/> it builds on: no EF, no clock, no knowledge of which
/// environment or provider this is. The caller loads the pin, the labels and the rule; this only
/// combines them. That keeps the one decision that stands between a merge request and production
/// exercisable without a database, and keeps it in exactly one place rather than re-derived at each
/// gate.
/// </para>
/// <para>
/// A pin wins outright, in whichever direction it points. It is the escape hatch a label gate needs
/// because labels cannot always be acquired - a merged merge request has left the poller's
/// open-request listing, so an approval that arrives after the merge may never be observable from
/// labels at all. Without letting a person override, the first production rule would wedge every task
/// holding such a merge request. And because a pin can also deny, it doubles as a hold: a person can
/// keep a merge request out even though its labels would admit it.
/// </para>
/// </remarks>
public static class ReadinessEvaluator
{
    /// <summary>
    /// Evaluates readiness. A pin decides if present; otherwise the labels decide against the rule.
    /// </summary>
    /// <param name="mergeRequestLabels">The merge request's labels, already normalized.</param>
    /// <param name="ruleLabels">The environment's configured labels, already normalized.</param>
    /// <param name="rule">The environment's rule.</param>
    /// <param name="pinnedReady">
    /// A person's decision for this merge request in this environment, or null when none was made.
    /// True admits, false holds; either way it is the answer and the labels are not consulted.
    /// </param>
    /// <returns>The decision and the source that produced it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rule is a value <see cref="ReadinessResolver"/> does not know.</exception>
    public static ReadinessDecision Evaluate(
        IReadOnlyCollection<string> mergeRequestLabels,
        IReadOnlyCollection<string> ruleLabels,
        ReadyRule rule,
        bool? pinnedReady)
    {
        if (pinnedReady is { } pinned)
            return new ReadinessDecision(pinned, ReadinessSource.Pin);

        return new ReadinessDecision(
            ReadinessResolver.IsReady(mergeRequestLabels, ruleLabels, rule),
            ReadinessSource.Labels);
    }
}
