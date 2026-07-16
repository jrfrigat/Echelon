using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;

namespace ReleaseOrchestrator.Infrastructure.Vcs;

public class VcsService(AppDbContext db, IVcsApiClient apiClient, TokenProtector protector) : IVcsService
{
    private static readonly Dictionary<string, MergeRequestStatus> StateMap = new()
    {
        ["opened"] = MergeRequestStatus.Opened,
        ["merged"] = MergeRequestStatus.Merged,
        ["closed"] = MergeRequestStatus.Closed
    };

    public async Task SyncMergeRequestAsync(Guid repositoryId, string externalMrId, CancellationToken ct)
    {
        var repo = await db.Repositories
            .Include(r => r.Connection)
            .FirstOrDefaultAsync(r => r.Id == repositoryId, ct)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found");

        var token = protector.Unprotect(repo.Connection.EncryptedAccessToken);
        var info = await apiClient.GetMergeRequestAsync(repo.Connection.ApiUrl, token, repo.ExternalId, externalMrId, ct);
        if (info is null) return;

        var existing = await db.MergeRequests
            .FirstOrDefaultAsync(mr => mr.RepositoryId == repositoryId && mr.ExternalId == externalMrId, ct);

        if (existing is null)
        {
            var taskExternalId = BranchTaskParser.ParseTaskId(info.SourceBranch);
            Guid? taskId = null;
            if (taskExternalId is not null)
                taskId = (await db.Tasks.FirstOrDefaultAsync(t => t.ExternalId == taskExternalId, ct))?.Id;

            db.MergeRequests.Add(new MergeRequest
            {
                Id = Guid.NewGuid(),
                ExternalId = externalMrId,
                RepositoryId = repositoryId,
                SourceBranch = info.SourceBranch,
                TargetBranch = info.TargetBranch,
                TaskId = taskId,
                Status = StateMap.GetValueOrDefault(info.State, MergeRequestStatus.Opened),
                CreatedAt = info.CreatedAt,
                MergedAt = info.MergedAt
            });
        }
        else
        {
            existing.Status = StateMap.GetValueOrDefault(info.State, existing.Status);
            existing.MergedAt = info.MergedAt;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<MergeRequestDto?> GetMergeRequestAsync(string connectionName, string projectPath, string iid, CancellationToken ct)
    {
        var conn = await db.VcsConnections.FirstOrDefaultAsync(c => c.Name == connectionName, ct);
        if (conn is null) return null;

        var token = protector.Unprotect(conn.EncryptedAccessToken);
        var info = await apiClient.GetMergeRequestAsync(conn.ApiUrl, token, projectPath, iid, ct);
        if (info is null) return null;

        var repo = await db.Repositories.FirstOrDefaultAsync(r => r.ConnectionId == conn.Id && r.ExternalId == projectPath, ct);
        var mr = repo is null ? null : await db.MergeRequests.FirstOrDefaultAsync(m => m.RepositoryId == repo.Id && m.ExternalId == iid, ct);

        return mr is null ? null : new MergeRequestDto(mr.Id, mr.ExternalId, mr.SourceBranch, mr.TargetBranch, mr.RepositoryId, mr.TaskId, mr.Status, mr.CreatedAt, mr.MergedAt);
    }
}
