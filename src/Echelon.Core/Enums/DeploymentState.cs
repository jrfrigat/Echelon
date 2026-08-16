namespace Echelon.Core.Enums;

/// <summary>
/// What the orchestrator has done with a merge request in a given environment.
/// </summary>
/// <remarks>
/// The orchestrator's truth, kept separate from <see cref="MergeRequestStatus"/> (the ingestion
/// truth of what the provider reports). Tracked per (merge request, environment): the same merge
/// request can be <see cref="Deployed"/> in staging and <see cref="NotStarted"/> in prod
///.
/// </remarks>
public enum DeploymentState
{
    /// <summary>Nothing has been attempted in this environment.</summary>
    NotStarted = 1,

    /// <summary>A rollout has taken the single-writer claim and is about to deploy.</summary>
    Claimed = 2,

    /// <summary>The deploy strategy has started; for async strategies it may still be running.</summary>
    Deploying = 3,

    /// <summary>Deployed successfully in this environment.</summary>
    Deployed = 4,

    /// <summary>The deploy failed and needs a retry or a fix.</summary>
    Failed = 5,

    /// <summary>Deliberately not deployed (already done, or skipped by an operator).</summary>
    Skipped = 6
}
