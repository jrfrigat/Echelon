using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Application.Contracts.Messages;

public record MrStatusChanged(
    string ConnectionName,
    string RepositoryExternalId,
    string ExternalMrId,
    MergeRequestStatus NewStatus,
    DateTime ChangedAt,
    string Source = "",
    string EventId = "") : IMessage, IHasEventIdentity;
