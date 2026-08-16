namespace Echelon.Core.Enums;

/// <summary>Where a readiness answer came from, so an operator can see why they were let in or refused.</summary>
public enum ReadinessSource
{
    /// <summary>Derived from the merge request's labels against the environment's rule.</summary>
    Labels = 1,

    /// <summary>
    /// A person decided, overriding the labels in whichever direction they chose.
    /// </summary>
    /// <remarks>
    /// Needed because labels cannot always be acquired: a merged merge request is not returned by the
    /// poller's open-request listing, so an approval that arrives after the merge may never be
    /// observable from labels at all. Without a pin, the first production rule would wedge every task
    /// holding such a merge request, permanently.
    /// </remarks>
    Pin = 2
}
