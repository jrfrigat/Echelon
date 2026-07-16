namespace ReleaseOrchestrator.Core.Entities;

public class Stack
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public ICollection<RepositoryStack> RepositoryStacks { get; set; } = [];
    public ICollection<StackDependency> DependentOn { get; set; } = [];
    public ICollection<StackDependency> RequiredBy { get; set; } = [];
}
