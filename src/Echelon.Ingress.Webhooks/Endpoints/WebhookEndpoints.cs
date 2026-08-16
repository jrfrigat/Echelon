using Microsoft.Extensions.DependencyInjection;
using Rebus.Bus;
using Echelon.Providers.Abstractions.Ingestion;

namespace Echelon.Ingress.Webhooks.Endpoints;

/// <summary>
/// Mounts one webhook route per registered provider, from the providers' own declarations.
/// </summary>
/// <remarks>
/// This is what "the provider owns its webhook" comes to in the host: the host names no provider. It
/// asks the container which parsers are registered, reads each one's
/// <see cref="WebhookEndpointDescriptor"/>, and mounts <c>/webhooks/{segment}/{connectionName}</c>
/// for it. Adding a provider adds a parser registration and nothing here changes. The generic
/// handler keeps only what every provider shares: name validation, secret lookup, the
/// authenticate-then-parse call, and sending the results to Core.
/// </remarks>
public static class WebhookEndpoints
{
    /// <summary>Maps a webhook endpoint for every registered <see cref="IWebhookParser"/>.</summary>
    /// <param name="app">The application to map routes on.</param>
    /// <returns>The same app, for chaining.</returns>
    public static WebApplication MapProviderWebhooks(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The registrations exist because keyed DI cannot enumerate its keys. One route per parser;
        // a duplicate route segment across two providers throws here at startup, which is the loud
        // failure we want rather than a silently shadowed endpoint.
        var registrations = app.Services.GetServices<WebhookParserRegistration>();

        foreach (var registration in registrations)
        {
            var parser = app.Services.GetRequiredKeyedService<IWebhookParser>(registration.ProviderType);
            var descriptor = parser.Descriptor;

            app.MapPost(
                    $"/webhooks/{descriptor.RouteSegment}/{{connectionName}}",
                    (string connectionName, HttpContext ctx, IBus bus, IConfiguration config, TimeProvider clock)
                        => HandleAsync(parser, descriptor, connectionName, ctx, bus, config, clock))
               // Unique per provider (the key is unique), so the OpenAPI operationId does not collide.
               .WithName($"{descriptor.ProviderType}Webhook");
        }

        return app;
    }

    private static async Task<IResult> HandleAsync(
        IWebhookParser parser,
        WebhookEndpointDescriptor descriptor,
        string connectionName,
        HttpContext ctx,
        IBus bus,
        IConfiguration config,
        TimeProvider clock)
    {
        var ct = ctx.RequestAborted;

        // A malformed name is answered exactly as a wrong token is: 401, no body. Unknown-connection
        // (a well-formed name with no configured secret) reaches Authenticate and fails closed there,
        // to the same 401 - so neither the connection list nor the secret can be probed by the
        // difference in response.
        var name = WebhookConnectionName.Sanitize(connectionName);
        if (name is null)
            return Results.Unauthorized();

        var secret = config[$"Webhooks:{descriptor.SecretConfigSection}:{name}:Token"];

        var body = await ReadBodyAsync(ctx.Request, ct);
        var request = new WebhookRequest(name, body, ReadHeaders(ctx.Request));

        // Verification is the provider's: it knows which header carries the secret and how to check
        // it. Read the body first, because a provider that verifies by signing the body needs it.
        if (!parser.Authenticate(request, secret))
            return Results.Unauthorized();

        IReadOnlyList<IngestionEvent> events;
        try
        {
            events = parser.Parse(request);
        }
        catch (WebhookPayloadException ex)
        {
            // Malformed payload: 400, so a sender genuinely sending garbage learns it. Distinct from
            // a parser returning nothing (an event kind we do not model), which is the 200 below.
            return Results.BadRequest(new { error = ex.Message });
        }

        // The host owns the one canonical Source spelling; the parser never sets it.
        var source = $"{descriptor.SourcePrefix}/{name}";
        foreach (var ev in events)
            await bus.Send(IngestionEventMapper.ToMessage(ev, name, source, clock));

        return Results.Ok();
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private static Dictionary<string, string> ReadHeaders(HttpRequest request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Headers)
            // First value, matching the old handler's .FirstOrDefault(); a provider reads
            // single-valued headers (a token, a delivery id).
            headers[key] = value.Count > 0 ? value[0] ?? string.Empty : string.Empty;

        return headers;
    }
}
