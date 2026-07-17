using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rebus.Config;
using Rebus.Retry.Simple;
using Rebus.Routing.TypeBased;
using Rebus.ServiceProvider;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Infrastructure.Queue.Consumers;

namespace ReleaseOrchestrator.Infrastructure.Queue;

/// <summary>
/// Wires the message bus. Rebus, over RabbitMQ.
/// </summary>
/// <remarks>
/// One input queue for the whole process, which is the Rebus model and fits a modular monolith:
/// every handler in the process reads from it, rather than a queue per handler. All six message
/// types live in one assembly and are routed to that queue, so a caller anywhere — the ingress, a
/// controller, a handler forwarding to the next step — reaches it with <c>bus.Send</c> and never
/// has to name a destination.
///
/// <para>
/// <c>Send</c>, not <c>Publish</c>: every message here has exactly one handler, so this is
/// point-to-point command dispatch, not fan-out. That choice is what lets the routing map do the
/// addressing and keeps subscription storage out of the picture entirely.
/// </para>
/// </remarks>
public static class MessagingSetup
{
    /// <summary>The process's single input queue. Handlers read it; senders route to it.</summary>
    private const string InputQueue = MessageRouting.InputQueue;

    /// <summary>Registers the bus and its six handlers for the Core process.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="config">Application configuration.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddMessaging(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var connectionString = RabbitMqConnectionString(config);
        var prefetch = config.GetValue("Queue:PrefetchCount", 16);

        services.AddRebusHandler<MrOpenedConsumer>();
        services.AddRebusHandler<MrStatusChangedConsumer>();
        services.AddRebusHandler<TaskCreatedConsumer>();
        services.AddRebusHandler<TaskStatusChangedConsumer>();
        services.AddRebusHandler<TaskSyncConsumer>();
        services.AddRebusHandler<ReleasePlanRecalculationConsumer>();

        services.AddRebus(configure => configure
            .Transport(t => t.UseRabbitMq(connectionString, InputQueue).Prefetch(prefetch))
            .Routing(r => r.TypeBased().MapAssemblyDerivedFrom<IMessage>(InputQueue))
            .Options(o =>
            {
                // Five delivery attempts, then the error queue — the message is parked, never lost,
                // for an operator to inspect or replay. This is where MassTransit's two retry tiers
                // collapse into one, and the departures are deliberate:
                //
                //   - No exponential backoff. Immediate retries are immediate here. The race they
                //     exist for — a status webhook overtaking its opened webhook — resolves in
                //     milliseconds, so a backoff would only slow the good case.
                //   - No separate delayed-redelivery tier for a minutes-long tracker outage. Rebus
                //     can do it (second-level retries + Defer), but it needs a timeout store, and
                //     the backstop already exists: TaskReconciliationService re-syncs on a timer, so
                //     a task that lands in the error queue during an outage is picked up on the next
                //     sweep. The error queue plus that sweep is the safety net, not a lost edge.
                //   - No kill switch. Rebus has no circuit breaker, and at one replica with
                //     idempotent handlers the failure it guards against — the whole pool hammering a
                //     downed dependency — is a single worker set retrying, which the attempt cap and
                //     the error queue already bound. Worth revisiting if this ever scales out.
                o.RetryStrategy(maxDeliveryAttempts: 5);
                o.SetNumberOfWorkers(config.GetValue("Queue:Workers", 4));
                o.SetMaxParallelism(prefetch);
            }));

        return services;
    }

    /// <summary>
    /// Builds the AMQP connection string, from a full URI if given or from the parts otherwise.
    /// </summary>
    /// <remarks>
    /// The parts (Host/Username/Password) are what docker-compose.yml already sets, so composing
    /// them here keeps those settings working unchanged. A full <c>Queue:ConnectionString</c> wins
    /// when present, for a deployment that would rather hand over one URI — TLS, a vhost, cluster
    /// hosts — than have this assemble it.
    /// </remarks>
    internal static string RabbitMqConnectionString(IConfiguration config)
    {
        var full = config["Queue:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(full)) return full;

        var host = config["Queue:Host"] ?? "localhost";
        var port = config.GetValue("Queue:Port", 5672);
        var user = Required(config, "Queue:Username");
        var password = Required(config, "Queue:Password");
        var vhost = config["Queue:VirtualHost"] ?? "/";

        // Escaped, so a password with a reserved character does not corrupt the URI. The vhost is a
        // path segment, and "/" — RabbitMQ's default vhost — must reach the server as "%2f", not as
        // a bare slash that would read as an empty vhost name.
        return $"amqp://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@{host}:{port}/{Uri.EscapeDataString(vhost)}";
    }

    private static string Required(IConfiguration config, string key) =>
        config[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. Set {key.Replace(':', '_').Replace("_", "__")} in the environment.");
}
