namespace Echelon.Application.DTOs;

/// <summary>
/// A dependency the plan could not honour. A non-empty list means the deploy order violates a
/// declared constraint - operators must see this, never a silent plan. Shared by the per-task
/// <see cref="RolloutPlanDto"/>.
/// </summary>
public record PlanConflictDto(
    string Kind,
    Guid FromMergeRequestId,
    Guid ToMergeRequestId,
    string Reason);
