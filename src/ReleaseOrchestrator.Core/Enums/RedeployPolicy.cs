namespace ReleaseOrchestrator.Core.Enums;

/// <summary>
/// Whether a merge request already deployed to an environment may be deployed there again.
/// </summary>
/// <remarks>
/// No member is 0, for the reason that matters most in this file: a repository whose policy was never
/// configured must not read as "redeploy freely" because a column defaulted to zero. The safe answer
/// has to come from an explicit value, not from the absence of one.
/// </remarks>
public enum RedeployPolicy
{
    /// <summary>
    /// Take the environment's own default. The environment supplies <see cref="Once"/> unless an
    /// operator deliberately made it otherwise.
    /// </summary>
    Inherit = 1,

    /// <summary>
    /// Deployed once, and not again. What production wants, and what everything falls back to.
    /// </summary>
    Once = 2,

    /// <summary>
    /// May be deployed repeatedly - a test environment where the same branch is pushed over and over.
    /// </summary>
    /// <remarks>
    /// This alone does not cause a redeploy. The launch must also ask for one explicitly; this only
    /// makes the request permissible. Two independent conditions, so neither a stray configuration
    /// change nor a stray button can redeploy on its own.
    /// </remarks>
    Always = 3
}
