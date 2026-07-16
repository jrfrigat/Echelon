namespace ReleaseOrchestrator.Core.Entities;

public class RepositoryStack
{
    public Guid RepositoryId { get; set; }
    public Guid StackId { get; set; }

    public Repository Repository { get; set; } = null!;
    public Stack Stack { get; set; } = null!;
}
