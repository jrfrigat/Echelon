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
    Task<ReleasePlanDto> ImportFromYamlAsync(string yaml, bool force = false, CancellationToken ct = default);
    Task<string> ExportToYamlAsync(Guid planId, CancellationToken ct = default);
    Task<ReleasePlanDto> ReorderStagesAsync(Guid planId, List<Guid> orderedStageIds, CancellationToken ct = default);
    Task<ReleasePlanDto> MoveItemAsync(Guid planId, Guid itemId, Guid targetStageId, CancellationToken ct = default);
    Task<ReleasePlanDto> AddItemAsync(Guid planId, Guid stageId, Guid mrId, CancellationToken ct = default);
    Task<ReleasePlanDto> RemoveItemAsync(Guid planId, Guid itemId, CancellationToken ct = default);
}
