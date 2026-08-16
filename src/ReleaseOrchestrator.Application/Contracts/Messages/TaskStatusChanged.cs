namespace ReleaseOrchestrator.Application.Contracts.Messages;

/// <summary>
/// A tracker issue's new status. Scoped by <see cref="TrackerConnectionName"/> because
/// ExternalId is only unique within a tracker - the product is multi-tracker by design,
/// so TASK-123 may exist in several of them.
/// </summary>
public record TaskStatusChanged(
    string TrackerConnectionName,
    string ExternalId,
    string NewStatus,
    DateTime? ClosedAt,
    string Source = "",
    string EventId = "") : IMessage, IHasEventIdentity;
