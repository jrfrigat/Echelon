using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Core.Entities;

public class StackDependency
{
    public Guid Id { get; set; }
    public Guid FromStackId { get; set; }
    public Guid ToStackId { get; set; }
    public StackDependencyType Type { get; set; }

    public Stack FromStack { get; set; } = null!;
    public Stack ToStack { get; set; } = null!;
}
