namespace Echelon.Application.Contracts.Messages;

/// <summary>
/// Asks for every active rollout plan to be rebuilt from the current atlas.
/// </summary>
/// <remarks>
/// Sent by any ingestion path that changed something a plan is derived from, rather than by each
/// path working out which plans it affected - a merge request moving can reorder the plan of any
/// task whose closure reaches it, and that is not knowable from the event. Deliberately not
/// deduplicated: two events that each changed the atlas both need the rebuild.
/// </remarks>
/// <param name="RequestedAt">When the change that prompted the rebuild was processed. UTC.</param>
/// <param name="Reason">A human note naming what changed, for the log.</param>
public record ReleasePlanRecalculationRequested(DateTime RequestedAt, string? Reason) : IMessage;
