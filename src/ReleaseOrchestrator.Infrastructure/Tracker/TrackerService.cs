using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;

namespace ReleaseOrchestrator.Infrastructure.Tracker;

public class TrackerService(AppDbContext db, ITrackerApiClient apiClient, TokenProtector protector) : ITrackerService
{
    public async Task SyncTaskAsync(Guid trackerConnectionId, string externalTaskId, CancellationToken ct)
    {
        var conn = await db.TrackerConnections.FindAsync([trackerConnectionId], ct)
            ?? throw new InvalidOperationException($"TrackerConnection {trackerConnectionId} not found");

        var token = protector.Unprotect(conn.EncryptedAccessToken);
        var info = await apiClient.GetIssueAsync(conn.ApiUrl, conn.OrgId ?? string.Empty, token, externalTaskId, ct);
        if (info is null) return;

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.TrackerConnectionId == trackerConnectionId && t.ExternalId == externalTaskId, ct);

        if (task is null)
        {
            task = new TaskItem
            {
                Id = Guid.NewGuid(),
                ExternalId = externalTaskId,
                Title = info.Summary,
                Status = info.StatusKey,
                ClosedAt = info.ResolvedAt,
                TrackerConnectionId = trackerConnectionId
            };
            db.Tasks.Add(task);
        }
        else
        {
            task.Status = info.StatusKey;
            task.ClosedAt = info.ResolvedAt;
        }

        await db.SaveChangesAsync(ct);

        await SyncDependenciesAsync(conn, task, token, ct);
    }

    public Task<string?> ParseTaskIdFromBranchAsync(string branchName, CancellationToken ct)
        => Task.FromResult(BranchTaskParser.ParseTaskId(branchName));

    private async Task SyncDependenciesAsync(TrackerConnection conn, TaskItem task, string token, CancellationToken ct)
    {
        var deps = await apiClient.GetIssueDependenciesAsync(conn.ApiUrl, conn.OrgId ?? string.Empty, token, task.ExternalId, ct);
        var existing = await db.TaskDependencies.Where(d => d.DependentTaskId == task.Id).ToListAsync(ct);
        db.TaskDependencies.RemoveRange(existing);

        foreach (var dep in deps)
        {
            var depTask = await db.Tasks.FirstOrDefaultAsync(t => t.TrackerConnectionId == conn.Id && t.ExternalId == dep.DependsOnKey, ct);
            if (depTask is null) continue;

            db.TaskDependencies.Add(new TaskDependency
            {
                Id = Guid.NewGuid(),
                DependentTaskId = task.Id,
                DependsOnTaskId = depTask.Id
            });
        }

        await db.SaveChangesAsync(ct);
    }

}
