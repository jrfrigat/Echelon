using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using ReleaseOrchestrator.Application.Contracts.Messages;
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
        CancellationToken ct)
    {
        var token = httpContext.Request.Headers["X-Tracker-Token"].FirstOrDefault();
        var expected = config[$"Webhooks:Tracker:{connectionName}:Token"];
        if (string.IsNullOrEmpty(expected) || token != expected)
            return Results.Unauthorized();

        switch (payload.Event)
        {
            case "issue:created":
                await publisher.Publish(new TaskCreated(
                    TrackerConnectionName: connectionName,
                    ExternalId: payload.Issue.Key,
                    Title: payload.Issue.Summary), ct);
                break;

            case "issue:updated":
            case "issue:statusUpdated":
                await publisher.Publish(new TaskStatusChanged(
                    TaskId: Guid.Empty,
                    ExternalId: payload.Issue.Key,
                    NewStatus: payload.Issue.Status.Key,
                    ClosedAt: IsClosedStatus(payload.Issue.Status.Key) ? DateTime.UtcNow : null), ct);
                break;
        }

        return Results.Ok();
    }

    private static bool IsClosedStatus(string statusKey) =>
        statusKey is "closed" or "cancelled" or "rejected";
}
