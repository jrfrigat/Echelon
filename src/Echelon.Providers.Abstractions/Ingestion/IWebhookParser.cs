namespace Echelon.Providers.Abstractions.Ingestion;

/// <summary>
/// Turns a provider's webhook delivery into normalized events, and decides whether the delivery is
/// authentic.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that lets a provider own its webhook. Everything provider-specific about a
/// delivery - the payload shape, which header carries the secret, which header carries the delivery
/// id, how a state string maps to a status, where labels live in the JSON - lives behind this port,
/// in the provider's own assembly. The host keeps only what is the same for every provider: the
/// route, resolving the connection's secret, and sending the resulting events onto the bus.
/// </para>
/// <para>
/// Registered as a keyed service under <see cref="WebhookEndpointDescriptor.ProviderType"/>, and
/// enumerated through <see cref="WebhookParserRegistration"/> - the same keyed-DI-plus-marker
/// pattern the provider factories already use, because keyed DI can resolve by key but cannot list
/// the keys that exist.
/// </para>
/// </remarks>
public interface IWebhookParser
{
    /// <summary>What this provider's webhook endpoint is called and how its secret is spelled.</summary>
    WebhookEndpointDescriptor Descriptor { get; }

    /// <summary>
    /// Decides whether a delivery is authentic, given the secret configured for its connection.
    /// </summary>
    /// <param name="request">The delivery.</param>
    /// <param name="secret">
    /// The secret configured for <see cref="WebhookRequest.ConnectionName"/>, or null when none is
    /// configured. Resolving it is the host's job (it owns configuration); verifying it is the
    /// provider's, because verification is provider-specific - a shared-secret header today, an
    /// HMAC over the body for a git host tomorrow.
    /// </param>
    /// <returns>
    /// True when the delivery may be processed. Must fail closed: a null or empty secret, or a
    /// missing credential on the request, is not authentic. Must run in constant time with respect
    /// to the secret - see <see cref="WebhookSignatures"/>.
    /// </returns>
    bool Authenticate(WebhookRequest request, string? secret);

    /// <summary>
    /// Parses a delivery into zero or more normalized events.
    /// </summary>
    /// <param name="request">The delivery, already authenticated.</param>
    /// <returns>
    /// The events to publish. Empty is a normal answer, not an error: a payload for an event this
    /// provider does not model (a comment, a state it does not map) yields nothing, and the host
    /// answers the sender 200 so it is not retried.
    /// </returns>
    /// <exception cref="WebhookPayloadException">
    /// The payload is malformed in a way the provider cannot make sense of - a required field is
    /// absent, the body is not the provider's JSON. The host turns this into a 400, distinct from
    /// the "understood but nothing to do" empty result.
    /// </exception>
    IReadOnlyList<IngestionEvent> Parse(WebhookRequest request);
}
