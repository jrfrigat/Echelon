namespace ReleaseOrchestrator.Application.Contracts.Messages;

/// <summary>
/// Asks for a task to be re-read from its tracker, including its dependency links.
///
/// Webhooks carry a status change but not the links, and trackers do not generally raise an
/// event when a link is added or removed - so the graph edges have to be pulled. Routing that
/// pull through the broker rather than doing it inline means it survives a restart and retries
/// on a tracker outage.
/// </summary>
public record TaskSyncRequested(
    string TrackerConnectionName,
    string ExternalId,
    string Reason) : IMessage;
