using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Application.Contracts.Messages;

/// <param name="Labels">
/// The merge request's labels as observed with this change. Carried so a merge event — the last
/// moment labels are visible before the merged request drops out of the open-request listing — can
/// record them for the readiness gate. Defaulted to empty so messages serialized before this field
/// existed still deserialize; an empty list on this path means "no label information in this event",
/// not "labels were removed", and the consumer leaves the stored set alone.
/// </param>
public record MrStatusChanged(
    string ConnectionName,
    string RepositoryExternalId,
    string ExternalMrId,
    MergeRequestStatus NewStatus,
    DateTime ChangedAt,
    IReadOnlyList<string> Labels,
    string Source = "",
    string EventId = "") : IMessage, IHasEventIdentity;
