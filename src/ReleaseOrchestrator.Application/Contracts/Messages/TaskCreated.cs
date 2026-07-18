namespace ReleaseOrchestrator.Application.Contracts.Messages;

public record TaskCreated(
    string TrackerConnectionName,
    string ExternalId,
    string Title,
    string Source = "",
    string EventId = "") : IMessage, IHasEventIdentity;
