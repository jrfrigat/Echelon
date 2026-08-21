namespace Echelon.Application.DTOs;

/// <summary>
/// A task's own facts, independent of any plan: what it is and where it sits in the tracker's
/// hierarchy.
/// </summary>
/// <remarks>
/// Separate from <see cref="RolloutPlanDto"/> because it has to be readable when there is no plan -
/// a task shows its parentage before anyone builds anything - and because the plan cannot carry all
/// of it: a plan is the closure of what the target waits on, and a task does not wait on its parent.
/// Opening a subtask would therefore show a tree its parent never appears in.
/// </remarks>
public record TaskDetailDto(
    Guid Id,
    string ExternalId,
    string Title,
    string Status,
    TaskRefDto? Parent,
    IReadOnlyList<TaskRefDto> Children);
