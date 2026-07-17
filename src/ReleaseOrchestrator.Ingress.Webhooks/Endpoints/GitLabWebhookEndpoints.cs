using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Rebus.Bus;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Ingress.Webhooks.Models;
using ReleaseOrchestrator.Providers.GitLab;

namespace ReleaseOrchestrator.Ingress.Webhooks.Endpoints;

public static class GitLabWebhookEndpoints
{
    public static IEndpointRouteBuilder MapGitLabWebhooks(this IEndpointRouteBuilder app)
    {
        // No WithOpenApi(): deprecated in .NET 10 (ASPDEPR002). The endpoint is still described
        // by the document generator through its name and typed results.
        app.MapPost("/webhooks/gitlab/{connectionName}", HandleAsync)
           .WithName("GitLabWebhook");

        return app;
    }

    private static async Task<IResult> HandleAsync(
        string connectionName,
        GitLabMrPayload payload,
        HttpContext httpContext,
        IBus bus,
        IConfiguration config,
        TimeProvider clock,
        CancellationToken ct)
    {
        var name = WebhookConnectionName.Sanitize(connectionName);
        var expected = name is null ? null : config[$"Webhooks:GitLab:{name}:Token"];
        var token = httpContext.Request.Headers["X-Gitlab-Token"].FirstOrDefault();

        // Identical answer for an unknown connection and a wrong token, so neither the
        // connection list nor the secret can be probed.
        if (!WebhookTokens.Matches(token, expected))
            return Results.Unauthorized();

        if (payload.ObjectKind != "merge_request")
            return Results.Ok();

        var attributes = payload.ObjectAttributes;
        if (payload.Project?.PathWithNamespace is not { Length: > 0 } repositoryExternalId
            || attributes?.Iid is not { } iid
            || attributes.State is not { Length: > 0 } state)
            return Results.BadRequest(new
            {
                error = "payload requires project.path_with_namespace, object_attributes.iid and object_attributes.state"
            });

        // GitLab's own dictionary, from GitLab's adapter — the same one the sync path uses, so
        // both arrival paths cannot disagree about what "merged" means.
        var status = GitLabMergeRequestState.FromState(state);
        if (status is null)
            return Results.Ok();   // A state we do not model; nothing to record.

        var externalMrId = iid.ToString();

        if (status == MergeRequestStatus.Opened)
        {
            // Published for every open-state event, not only the first. GitLab re-sends on
            // label changes, pushes and reopens; the consumer upserts, so a reopened MR
            // rejoins the plan and a newly labelled one enters it.
            await bus.Send(new MrOpened(
                ConnectionName: connectionName,
                RepositoryExternalId: repositoryExternalId,
                ExternalMrId: externalMrId,
                SourceBranch: attributes.SourceBranch ?? string.Empty,
                TargetBranch: attributes.TargetBranch ?? string.Empty,
                TaskExternalId: GitLabBranchTaskParser.ParseTaskId(attributes.SourceBranch),
                Labels: ExtractLabels(payload)));
        }
        else
        {
            await bus.Send(new MrStatusChanged(
                ConnectionName: connectionName,
                RepositoryExternalId: repositoryExternalId,
                ExternalMrId: externalMrId,
                NewStatus: status.Value,
                ChangedAt: clock.GetUtcNow().UtcDateTime));
        }

        return Results.Ok();
    }

    /// <summary>GitLab populates labels at the top level, and for some events only under object_attributes.</summary>
    private static IReadOnlyList<string> ExtractLabels(GitLabMrPayload payload) =>
        (payload.Labels ?? payload.ObjectAttributes?.Labels ?? [])
            .Select(l => l.Title)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
