namespace ReleaseOrchestrator.Core.Entities;

public class Repository
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public Guid ConnectionId { get; set; }

    public VcsConnection Connection { get; set; } = null!;
    public ICollection<MergeRequest> MergeRequests { get; set; } = [];
    public ICollection<RepositoryStack> RepositoryStacks { get; set; } = [];
}
