namespace ReleaseOrchestrator.Application.Contracts.Messages;

/// <summary>
/// A task observed in a tracker. Published on first sight and on every re-observation alike - the
/// consumer upserts, so this carries current state rather than announcing a creation.
/// </summary>
/// <param name="TrackerConnectionName">The tracker connection the observation came through.</param>
/// <param name="ExternalId">The tracker's own key for the task (the key merge requests name).</param>
/// <param name="Title">The task title.</param>
/// <param name="Source">Which ingestion path produced this, used to scope the event id.</param>
/// <param name="EventId">Deduplication identity; empty means "not deduplicated".</param>
public record TaskCreated(
    string TrackerConnectionName,
    string ExternalId,
    string Title,
    string Source = "",
    string EventId = "") : IMessage, IHasEventIdentity;
