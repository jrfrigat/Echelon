namespace ReleaseOrchestrator.Core.Entities;

public class Repository
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public Guid ConnectionId { get; set; }

    /// <summary>
    /// Which tracker this repository's branch issue keys belong to.
    ///
    /// Repositories hang off a VCS connection and tasks off a tracker connection, with nothing
    /// joining them: an issue key parsed from a branch could only be matched globally, so in a
    /// multi-tracker setup — which this product exists to serve — the same key in two trackers
    /// made the link ambiguous and it was dropped rather than guessed at.
    ///
    /// Null keeps the old global match, which is fine for a single tracker.
    /// </summary>
    public Guid? TrackerConnectionId { get; set; }

    public VcsConnection Connection { get; set; } = null!;
    public TrackerConnection? TrackerConnection { get; set; }
    public ICollection<MergeRequest> MergeRequests { get; set; } = [];
    public ICollection<RepositoryStack> RepositoryStacks { get; set; } = [];
}
