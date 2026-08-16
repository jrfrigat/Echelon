namespace ReleaseOrchestrator.Ingress.Webhooks;

/// <summary>
/// Builds the ingress's AMQP connection string.
/// </summary>
/// <remarks>
/// The same shape as the Core process's builder, kept separate because the ingress does not
/// reference Infrastructure and should not - it is a webhook front door, not part of the domain.
/// The queue name is shared (see <see cref="Application.Contracts.Messages.MessageRouting"/>)
/// because a drift there fails silently; this string is not, because a wrong connection fails
/// loudly at startup.
/// </remarks>
internal static class IngressMessaging
{
    /// <summary>From a full <c>Queue:ConnectionString</c> if given, otherwise composed from the parts.</summary>
    public static string RabbitMqConnectionString(IConfiguration config)
    {
        var full = config["Queue:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(full)) return full;

        var host = config["Queue:Host"] ?? "localhost";
        var port = config.GetValue("Queue:Port", 5672);
        var user = Required(config, "Queue:Username");
        var password = Required(config, "Queue:Password");
        var vhost = config["Queue:VirtualHost"] ?? "/";

        return $"amqp://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@{host}:{port}/{Uri.EscapeDataString(vhost)}";
    }

    private static string Required(IConfiguration config, string key) =>
        config[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. Set {key.Replace(':', '_').Replace("_", "__")} in the environment.");
}
