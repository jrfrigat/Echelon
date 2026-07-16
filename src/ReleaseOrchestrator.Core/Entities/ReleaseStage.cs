namespace ReleaseOrchestrator.Core.Entities;

public class ReleaseStage
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public int Sequence { get; set; }
    public string? Name { get; set; }
    public bool IsManualOverride { get; set; }

    public ReleasePlan Plan { get; set; } = null!;
    public ICollection<StageItem> Items { get; set; } = [];
}
