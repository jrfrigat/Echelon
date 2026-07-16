namespace ReleaseOrchestrator.Application.Contracts.Messages;

public record TaskStatusChanged(
    Guid TaskId,
    string ExternalId,
    string NewStatus,
    DateTime? ClosedAt);
