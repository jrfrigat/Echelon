namespace ReleaseOrchestrator.Application.DTOs;

/// <summary>A task as it appears in the tasks list -- what an operator might roll out.</summary>
public record TaskListItemDto(
    Guid Id,
    string ExternalId,
    string Title,
    string Status,
    int MergeRequestCount,
    bool HasActivePlan);

/// <summary>Another task named from this one — its parent, or one of its children.</summary>
public record TaskRefDto(Guid Id, string ExternalId, string Title);

/// <summary>A repository in the default plan, in the wave it deploys in.</summary>
public record DefaultPlanRepositoryDto(Guid Id, string Name);

/// <summary>One wave of the default plan: repositories that deploy in parallel.</summary>
public record DefaultPlanWaveDto(int Sequence, IReadOnlyList<DefaultPlanRepositoryDto> Repositories);

/// <summary>An ordering rule the default plan could not honour — in practice, a cycle in the rules.</summary>
public record DefaultPlanConflictDto(string FromRepositoryName, string ToRepositoryName, string Kind, string Reason);

/// <summary>
/// The default rollout plan: the order repositories deploy in before any task's own dependencies
/// or hierarchy are taken into account.
/// </summary>
/// <remarks>
/// Derived from the repository-ordering rules, not stored. It is shown to operators as the answer to
/// "what do these rules actually mean", which a list of pairs does not answer on its own.
/// </remarks>
public record DefaultPlanDto(
    IReadOnlyList<DefaultPlanWaveDto> Waves,
    IReadOnlyList<DefaultPlanConflictDto> Conflicts);

/// <summary>
/// A task's own facts, independent of any plan: what it is and where it sits in the tracker's
/// hierarchy.
/// </summary>
/// <remarks>
/// Separate from <see cref="RolloutPlanDto"/> because it has to be readable when there is no plan —
/// a task shows its parentage before anyone builds anything — and because the plan cannot carry all
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

/// <summary>
/// A per-task rollout plan: the tree of the target's dependency closure plus the merge-request-level
/// execution waves.
/// </summary>
/// <remarks>
/// <see cref="Nodes"/> is the presentation tree (target and its prerequisite tasks); <see cref="Waves"/>
/// is the execution order, which is a merge-request property -- a task's merge requests can land in
/// different waves.
/// </remarks>
public record RolloutPlanDto(
    Guid Id,
    Guid TargetTaskId,
    string TargetTaskKey,
    int Version,
    string Source,
    string Status,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<PlanTaskNodeDto> Nodes,
    IReadOnlyList<PlanWaveDto> Waves,
    IReadOnlyList<PlanConflictDto> Conflicts);

/// <summary>A task in the plan tree, with the merge requests attached to it.</summary>
public record PlanTaskNodeDto(
    Guid TaskId,
    string TaskKey,
    string TaskTitle,
    bool IsTarget,
    IReadOnlyList<Guid> DependsOnTaskIds,
    IReadOnlyList<PlanItemDto> Items);

/// <summary>A merge request in the plan, with the wave it deploys in.</summary>
/// <param name="ManuallyIncluded">
/// True when an operator forced this merge request in rather than the derivation choosing it — worth
/// showing, because it is the one row in the plan that does not follow from the atlas.
/// </param>
public record PlanItemDto(
    Guid MergeRequestId,
    string MrExternalId,
    string RepositoryName,
    string SourceBranch,
    string TargetBranch,
    string MrStatus,
    int Wave,
    bool ManuallyIncluded);

/// <summary>One execution wave: merge requests that deploy in parallel.</summary>
public record PlanWaveDto(int Sequence, IReadOnlyList<Guid> MergeRequestIds);

/// <summary>
/// A mandatory ordering constraint an imported plan does not honour, named the way the document is.
/// </summary>
/// <param name="Kind">Which constraint: a task dependency or a hard repository link.</param>
/// <param name="From">The merge request that should deploy first, by its document key.</param>
/// <param name="To">The merge request that should wait.</param>
/// <remarks>
/// Keys rather than ids: this answers an operator looking at the YAML they just wrote, and a pair of
/// GUIDs would send them back to the database to find out which two lines to change.
/// </remarks>
public record PlanImportViolationDto(string Kind, string From, string To);

/// <summary>What validating or importing a plan document produced.</summary>
/// <param name="Accepted">Whether the document would be (or was) applied.</param>
/// <param name="Errors">Why it cannot be applied at all. Empty when it can.</param>
/// <param name="Violations">
/// Constraints the requested order breaks. Not errors: an operator may deploy against the declared
/// order, which is what <c>force</c> is for — but never without the plan recording it.
/// </param>
/// <param name="Plan">The plan as stored, on a successful import. Null for a validate.</param>
public record PlanImportDto(
    bool Accepted,
    IReadOnlyList<string> Errors,
    IReadOnlyList<PlanImportViolationDto> Violations,
    RolloutPlanDto? Plan);
