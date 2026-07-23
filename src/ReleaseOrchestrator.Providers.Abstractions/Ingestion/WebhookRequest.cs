namespace ReleaseOrchestrator.Providers.Abstractions.Ingestion;

/// <summary>
/// A webhook delivery, in a form a provider parser can read without depending on ASP.NET.
/// </summary>
/// <remarks>
/// <para>
/// This is the boundary that lets a webhook endpoint live with its provider while the provider
/// stays a plain library. The host reads the HTTP request and hands the parser this: the raw body,
/// a header lookup, and the connection the URL named. Deliberately <b>not</b> an
/// <c>HttpRequest</c> — the moment a provider takes one of those it needs a
/// <c>FrameworkReference</c> to ASP.NET and stops being the portable adapter the architecture is
/// built to keep replaceable.
/// </para>
/// <para>
/// The body is bytes rather than a deserialized object because deserialization is the provider's
/// job: only the provider knows its payload shape, and a signature check (for the providers that
/// use one) must run over the exact bytes received, not over a re-serialization of a parsed object.
/// </para>
/// </remarks>
public sealed class WebhookRequest
{
    private readonly IReadOnlyDictionary<string, string> _headers;

    /// <summary>Creates a request from the pieces the host extracted.</summary>
    /// <param name="connectionName">
    /// The connection the route named, already sanitized by the host. The parser treats it as a
    /// trusted identifier — the host is responsible for rejecting a malformed one before here.
    /// </param>
    /// <param name="body">The raw request body.</param>
    /// <param name="headers">
    /// The request headers, keyed case-insensitively. A provider looks up its own header names
    /// (<c>X-Gitlab-Token</c>, <c>X-Gitlab-Event-UUID</c>, …); the case-insensitive lookup matches
    /// how HTTP treats header names and how every provider spelled them inconsistently before.
    /// </param>
    public WebhookRequest(
        string connectionName,
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string> headers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentNullException.ThrowIfNull(headers);

        ConnectionName = connectionName;
        Body = body;
        // Copied into a case-insensitive map regardless of what the caller passed: HTTP header
        // names are case-insensitive, and IReadOnlyDictionary does not expose its comparer, so
        // there is no way to tell whether the caller's lookup already ignores case.
        _headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The connection the route named, sanitized.</summary>
    public string ConnectionName { get; }

    /// <summary>The raw request body.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>The value of a header, or null when it was not sent.</summary>
    /// <param name="name">The header name; matched case-insensitively.</param>
    public string? Header(string name) => _headers.GetValueOrDefault(name);
}
