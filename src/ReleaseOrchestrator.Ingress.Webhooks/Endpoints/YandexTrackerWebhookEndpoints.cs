using Rebus.Bus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Ingress.Webhooks.Models;
using ReleaseOrchestrator.Providers.YandexTracker;

namespace ReleaseOrchestrator.Ingress.Webhooks.Endpoints;

public static class YandexTrackerWebhookEndpoints
{
    public static IEndpointRouteBuilder MapYandexTrackerWebhooks(this IEndpointRouteBuilder app)
    {
        // No WithOpenApi(): deprecated in .NET 10 (ASPDEPR002).
        app.MapPost("/webhooks/tracker/{connectionName}", HandleAsync)
           .WithName("YandexTrackerWebhook");

        return app;
    }

    private static async Task<IResult> HandleAsync(
        string connectionName,
        YandexTrackerEventPayload payload,
        HttpContext httpContext,
        IBus bus,
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

        // Source records provenance; EventId is left empty because Yandex.Tracker does not send a
        // stable per-delivery id header, so these events are not deduplicated yet. Add one here if a
        // delivery-id header is confirmed against a live tracker (docs/issues/008-ingestion-and-messaging.md).
        var source = $"yandex/{connectionName}";

        switch (payload.Event)
        {
            case "issue:created":
                await bus.Send(new TaskCreated(
                    TrackerConnectionName: connectionName,
                    ExternalId: issueKey,
                    Title: payload.Issue.Summary ?? string.Empty,
                    Source: source,
                    EventId: string.Empty));
                break;

            case "issue:updated":
            case "issue:statusUpdated":
                if (payload.Issue.Status?.Key is not { Length: > 0 } statusKey)
                    return Results.BadRequest(new { error = "status update requires issue.status.key" });

                await bus.Send(new TaskStatusChanged(
                    TrackerConnectionName: connectionName,
                    ExternalId: issueKey,
                    NewStatus: statusKey,
                    ClosedAt: YandexTrackerStatusRules.IsClosed(statusKey) ? clock.GetUtcNow().UtcDateTime : null,
                    Source: source,
                    EventId: string.Empty));
                break;
        }

        return Results.Ok();
    }
}
