using ReleaseOrchestrator.Application.DTOs;

namespace ReleaseOrchestrator.Application.Services;

public interface IReleasePlannerService
{
    Task<ReleasePlanDto> RecalculateAsync(CancellationToken ct = default);

    /// <summary>
    /// True when an automatic plan already reflects everything committed before
    /// <paramref name="requestedAt"/>, so a recalculation queued at that instant is
    /// redundant. This is what collapses an event burst into a single rebuild.
    /// </summary>
    Task<bool> IsPlanCurrentAsync(DateTime requestedAt, CancellationToken ct = default);
    Task<ReleasePlanDto?> GetActiveAsync(CancellationToken ct = default);
    Task<ReleasePlanDto?> GetByIdAsync(Guid planId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the active plan with one written by hand.
    /// </summary>
    /// <param name="yaml">The document. Must have a top-level <c>release_plan</c> key.</param>
    /// <param name="force">
    /// Skip <c>mr_id</c>s that resolve to nothing instead of refusing the document. Off by default:
    /// a plan silently missing a merge request is a deploy that looks complete and is not.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// An imported plan may violate a declared dependency — a release sometimes has to deploy
    /// against the order — so this does not reject one that does. Every violation is recorded in
    /// <see cref="ReleasePlanDto.Conflicts"/> instead, which is the same treatment a manual edit
    /// gets. What no plan may do is break a constraint and report itself clean.
    /// </remarks>
    Task<ReleasePlanDto> ImportFromYamlAsync(string yaml, bool force = false, CancellationToken ct = default);

    Task<string> ExportToYamlAsync(Guid planId, CancellationToken ct = default);

    // The editing operations below accept any arrangement the operator asks for and rescore the
    // plan against the graph, so Conflicts always describes the plan as stored. They used to accept
    // silently while import refused the same edit outright — the constraint an import could not get
    // past was one drag away.
    Task<ReleasePlanDto> ReorderStagesAsync(Guid planId, List<Guid> orderedStageIds, CancellationToken ct = default);
    Task<ReleasePlanDto> MoveItemAsync(Guid planId, Guid itemId, Guid targetStageId, CancellationToken ct = default);
    Task<ReleasePlanDto> AddItemAsync(Guid planId, Guid stageId, Guid mrId, CancellationToken ct = default);
    Task<ReleasePlanDto> RemoveItemAsync(Guid planId, Guid itemId, CancellationToken ct = default);
}
