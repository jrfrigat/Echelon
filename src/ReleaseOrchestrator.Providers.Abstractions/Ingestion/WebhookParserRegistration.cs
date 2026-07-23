namespace ReleaseOrchestrator.Providers.Abstractions.Ingestion;

/// <summary>
/// Declares that a webhook parser is registered under <paramref name="ProviderType"/>.
/// </summary>
/// <remarks>
/// The same reason <see cref="VcsProviderRegistration"/> exists: keyed DI resolves a service by key
/// but cannot enumerate the keys, and the host needs the list to mount one route per parser. Each
/// provider that registers an <see cref="IWebhookParser"/> also registers one of these.
/// </remarks>
/// <param name="ProviderType">The canonical key, as produced by <see cref="ProviderKey.Normalize"/>.</param>
public sealed record WebhookParserRegistration(string ProviderType);

/// <summary>
/// Raised by an <see cref="IWebhookParser"/> when a delivery cannot be parsed at all — a required
/// field is missing, or the body is not the provider's payload.
/// </summary>
/// <remarks>
/// Distinct from a parser returning an empty list, which means "understood, nothing to do" and is
/// answered 200. This means "malformed" and the host answers 400, so a sender that is genuinely
/// sending garbage learns it, while a sender delivering events this provider does not model is not
/// told it failed.
/// </remarks>
public sealed class WebhookPayloadException(string message) : Exception(message);
