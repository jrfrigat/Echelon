using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Core.Parsing;

/// <summary>
/// Decides whether a merge request's labels satisfy an environment's rule.
/// </summary>
/// <remarks>
/// <para>
/// Pure: no EF, no I/O, no clock, no knowledge of which environment this is or which provider spelled
/// the labels. That is deliberate — this is the single most consequential predicate in the product,
/// because it is what stands between unreviewed code and production, and it has to be exercisable
/// without a database.
/// </para>
/// <para>
/// The rule it enforces, stated once: <b>the absence of configuration is never readiness.</b> A gate
/// that is switched on but has no labels configured admits nothing. The tempting reading — "no
/// criteria, so everything passes" — is how an environment ends up ungated by omission, and it is
/// precisely the outcome this function exists to make impossible.
/// </para>
/// </remarks>
public static class ReadinessResolver
{
    /// <summary>
    /// True when <paramref name="mergeRequestLabels"/> satisfies <paramref name="rule"/> over
    /// <paramref name="ruleLabels"/>.
    /// </summary>
    /// <param name="mergeRequestLabels">The merge request's labels, already normalized.</param>
    /// <param name="ruleLabels">The environment's configured labels, already normalized.</param>
    /// <param name="rule">The environment's rule.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The rule is a value this function does not know. Thrown rather than defaulted: a new member
    /// added later must fail loudly here, not quietly pick an answer — and the wrong quiet answer
    /// would be "allowed".
    /// </exception>
    public static bool IsReady(
        IReadOnlyCollection<string> mergeRequestLabels,
        IReadOnlyCollection<string> ruleLabels,
        ReadyRule rule)
    {
        ArgumentNullException.ThrowIfNull(mergeRequestLabels);
        ArgumentNullException.ThrowIfNull(ruleLabels);

        return rule switch
        {
            // Someone deliberately said this environment has no gate. Recorded as a decision, and
            // gated on approval permission where it is set, rather than being what happens when
            // nobody configured anything.
            ReadyRule.NoGate => true,

            // Both branches require a non-empty rule set FIRST. Without that guard, All() over an
            // empty sequence is vacuously true and an unconfigured AllOf gate would admit every
            // merge request in the system -- the failure mode being guarded against, arriving through
            // a language default rather than a decision.
            ReadyRule.AnyOf => ruleLabels.Count > 0 && ruleLabels.Any(mergeRequestLabels.Contains),
            ReadyRule.AllOf => ruleLabels.Count > 0 && ruleLabels.All(mergeRequestLabels.Contains),

            _ => throw new ArgumentOutOfRangeException(
                nameof(rule), rule, "Unknown readiness rule; refusing to guess whether this may deploy.")
        };
    }
}
