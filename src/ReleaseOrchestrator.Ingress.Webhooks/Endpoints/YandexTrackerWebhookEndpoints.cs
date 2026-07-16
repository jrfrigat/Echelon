using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Ingress.Webhooks.Models;

namespace ReleaseOrchestrator.Ingress.Webhooks.Endpoints;

public static class YandexTrackerWebhookEndpoints
{
    public static IEndpointRouteBuilder MapYandexTrackerWebhooks(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/tracker/{connectionName}", HandleAsync)
           .WithName("YandexTrackerWebhook")
           .WithOpenApi();

        return app;
    }

    private static async Task<IResult> HandleAsync(
        string connectionName,
        YandexTrackerEventPayload payload,
        HttpContext httpContext,
        IPublishEndpoint publisher,
        IConfiguration config,
        TimeProvider clock,
        CancellationToken ct)
    {
        var name = WebhookConnectionName.Sanitize(connectionName);
        var expected = name is null ? null : config[$"Webhooks:Tracker:{name}:Token"];
        var token = httpContext.Request.Headers["X-Tracker-Token"].FirstOrDefault();

        if (!WebhookTokens.Matches(token, expected))
            return Results.Unauthorized();

        if (payload.Issue?.Key is not { Length: > 0 } issueKey)
            return Results.BadRequest(new { error = "payload requires issue.key" });

        switch (payload.Event)
        {
            case "issue:created":
                await publisher.Publish(new TaskCreated(
                    TrackerConnectionName: connectionName,
                    ExternalId: issueKey,
                    Title: payload.Issue.Summary ?? string.Empty), ct);
                break;

            case "issue:updated":
            case "issue:statusUpdated":
                if (payload.Issue.Status?.Key is not { Length: > 0 } statusKey)
                    return Results.BadRequest(new { error = "status update requires issue.status.key" });

                await publisher.Publish(new TaskStatusChanged(
                    TrackerConnectionName: connectionName,
                    ExternalId: issueKey,
                    NewStatus: statusKey,
                    ClosedAt: TaskStatusRules.IsClosed(statusKey) ? clock.GetUtcNow().UtcDateTime : null), ct);
                break;
        }

        return Results.Ok();
    }
}
