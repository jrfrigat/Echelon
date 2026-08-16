namespace Echelon.Core.Enums;

/// <summary>
/// How an environment decides whether a merge request is allowed into it.
/// </summary>
/// <remarks>
/// <para>
/// One field, so "there is a rule" and "there is no gate" are mutually exclusive by construction. A
/// separate boolean beside a label set would allow the vacuous combination - gate enabled, no labels
/// configured - whose only sensible reading is "nothing qualifies" but whose likely implementation is
/// "everything qualifies".
/// </para>
/// <para>
/// No member is 0. Nothing may rely on <c>default(ReadyRule)</c>: a column that was never written
/// must not silently mean "no gate", which is exactly how production ends up ungated by accident.
/// </para>
/// </remarks>
public enum ReadyRule
{
    /// <summary>
    /// Anything not closed may deploy here. A deliberate, recorded decision - never a default.
    /// </summary>
    /// <remarks>
    /// Reasonable for a scratch environment. Choosing it is an approval decision rather than a
    /// configuration one, which is why setting it is gated on approval permission rather than on
    /// ordinary config editing.
    /// </remarks>
    NoGate = 1,

    /// <summary>Ready when the merge request carries at least one of the configured labels.</summary>
    AnyOf = 2,

    /// <summary>Ready only when the merge request carries every configured label.</summary>
    AllOf = 3
}
