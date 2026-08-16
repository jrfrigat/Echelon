namespace ReleaseOrchestrator.Application.Contracts.Messages;

/// <summary>
/// The full branch list of one repository, as observed by a sweep.
/// </summary>
/// <remarks>
/// A snapshot rather than a per-branch event, deliberately: the consumer must be able to tell that a
/// branch is *gone* (merged and deleted, or abandoned), and a stream of "branch exists" messages never
/// says that. Carrying the whole list lets the consumer reconcile - upsert what is here, drop what is
/// not - which is the same shape the merge-request reconcile uses for labels, and for the same reason.
/// </remarks>
public record BranchesObserved(
    string ConnectionName,
    string RepositoryExternalId,
    IReadOnlyList<BranchesObserved.Branch> Branches,
    // Event identity for dedup: the id folds in the branch set, so an unchanged repository is dropped
    // by the inbox and only a real change costs any work.
    string Source = "",
    string EventId = "") : IMessage, IHasEventIdentity
{
    /// <summary>One branch as a sweep saw it.</summary>
    /// <remarks>
    /// Nested rather than a sibling record: it is this message's payload, not a message of its own, and
    /// every top-level record in this namespace is expected to be routable (MessageRoutingTests).
    /// </remarks>
    /// <param name="Name">The branch name, as the provider spells it.</param>
    /// <param name="IsMerged">Whether the provider reports it as merged into the default branch.</param>
    /// <param name="IsDefault">Whether it is the repository's default branch.</param>
    public record Branch(string Name, bool IsMerged, bool IsDefault = false);
}
