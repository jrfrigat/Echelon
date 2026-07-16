using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Ingress.Webhooks.Models;

namespace ReleaseOrchestrator.Ingress.Webhooks.Endpoints;

public static class GitLabWebhookEndpoints
{
    private static readonly Dictionary<string, MergeRequestStatus> StateMap = new()
    {
        ["opened"] = MergeRequestStatus.Opened,
        ["merged"] = MergeRequestStatus.Merged,
        ["closed"] = MergeRequestStatus.Closed
    };

    public static IEndpointRouteBuilder MapGitLabWebhooks(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/gitlab/{connectionName}", HandleAsync)
           .WithName("GitLabWebhook")
           .WithOpenApi();

        return app;
    }

    private static async Task<IResult> HandleAsync(
        string connectionName,
        GitLabMrPayload payload,
        HttpContext httpContext,
        IPublishEndpoint publisher,
        IConfiguration config,
        CancellationToken ct)
    {
        var token = httpContext.Request.Headers["X-Gitlab-Token"].FirstOrDefault();
        var expected = config[$"Webhooks:GitLab:{connectionName}:Token"];
        if (string.IsNullOrEmpty(expected) || token != expected)
            return Results.Unauthorized();

        if (payload.ObjectKind != "merge_request")
            return Results.Ok();

        var repositoryExternalId = payload.Project.PathWithNamespace;
        var externalMrId = payload.ObjectAttributes.Iid.ToString();
        var state = payload.ObjectAttributes.State;

        // Parse task id from branch name (e.g. feature/TASK-123-something)
        string? taskExternalId = ParseTaskId(payload.ObjectAttributes.SourceBranch);

        if (state == "opened")
        {
            await publisher.Publish(new MrOpened(
                ConnectionName: connectionName,
                RepositoryExternalId: repositoryExternalId,
                ExternalMrId: externalMrId,
                SourceBranch: payload.ObjectAttributes.SourceBranch,
                TargetBranch: payload.ObjectAttributes.TargetBranch,
                TaskExternalId: taskExternalId), ct);
        }
        else if (StateMap.TryGetValue(state, out var status))
        {
            await publisher.Publish(new MrStatusChanged(
                ConnectionName: connectionName,
                RepositoryExternalId: repositoryExternalId,
                ExternalMrId: externalMrId,
                NewStatus: status,
                ChangedAt: DateTime.UtcNow), ct);
        }

        return Results.Ok();
    }

    private static string? ParseTaskId(string branch)
    {
        // matches PROJ-123 patterns in branch names
        var match = System.Text.RegularExpressions.Regex.Match(branch, @"([A-Z]+-\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }
}
