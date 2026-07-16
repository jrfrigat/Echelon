namespace ReleaseOrchestrator.Core.Entities;

public class StageItem
{
    public Guid Id { get; set; }
    public Guid StageId { get; set; }
    public Guid MergeRequestId { get; set; }
    public bool ManualInclusion { get; set; }

    public ReleaseStage Stage { get; set; } = null!;
    public MergeRequest MergeRequest { get; set; } = null!;
}
