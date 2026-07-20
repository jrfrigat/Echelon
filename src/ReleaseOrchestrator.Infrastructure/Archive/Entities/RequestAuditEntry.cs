namespace ReleaseOrchestrator.Infrastructure.Archive.Entities;

/// <summary>
/// One HTTP request as it was served: what was called, by whom, what came back, and how long it took.
/// </summary>
/// <remarks>
/// <para>
/// Lives in the ARCHIVE database, not the operational one, and that is the load-bearing decision.
/// Audit volume is a multiple of real traffic, and putting it beside the release data would mean a
/// busy afternoon of polling competing with the rollout engine for the same transaction log — a
/// runaway audit table degrading the thing it exists to observe. It also means the credential that
/// writes the audit is not the credential that can erase it.
/// </para>
/// <para>
/// A plain POCO with no annotations, mapped fluently in <c>ArchiveDbContext</c>, matching the style
/// of the archived entities beside it rather than the attribute-first convention of the operational
/// model.
/// </para>
/// <para>
/// What is deliberately absent is as important as what is here: no headers, no request or response
/// body, no query string, no cookies. Those are not filtered — they are never read. There is
/// therefore no allowlist to misconfigure and no future endpoint that leaks by being forgotten.
/// The sign-in endpoint receives a cleartext password and every call from the app carries a bearer
/// token; the only safe way to hold that data is not to.
/// </para>
/// </remarks>
public class RequestAuditEntry
{
    /// <summary>
    /// Primary key, a time-ordered UUID (v7).
    /// </summary>
    /// <remarks>
    /// v7 rather than v4 on purpose: this table is append-only under sustained insert, and a random
    /// key fragments the index on every write. Time-ordered keys append.
    /// </remarks>
    public Guid Id { get; set; }

    /// <summary>Which host served it. Currently always the core API; the webhook host is not covered yet.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The machine or replica that served it, so replicas do not collapse into one identity.</summary>
    public string Instance { get; set; } = string.Empty;

    /// <summary>HTTP method.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// The matched route template, e.g. <c>api/tasks/{id:guid}/rollouts</c>.
    /// </summary>
    /// <remarks>
    /// This, not <see cref="Path"/>, is what summaries group by. Its cardinality is bounded by the
    /// number of endpoints; a path's is bounded by whatever callers type, which an anonymous
    /// stranger controls.
    /// </remarks>
    public string RoutePattern { get; set; } = string.Empty;

    /// <summary>
    /// The request path, with no query string ever.
    /// </summary>
    /// <remarks>
    /// A literal placeholder when the request matched the SPA fallback rather than a real endpoint,
    /// so an attacker-chosen string never reaches this column.
    /// </remarks>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// What kind of request it was: <c>Api</c>, <c>Auth</c>, <c>RoutingMiss</c> or <c>Other</c>.
    /// </summary>
    /// <remarks>
    /// Exists so a 200 from the catch-all fallback can never be counted as a healthy API call by a
    /// query written later by someone who does not know the fallback answers 200 for anything.
    /// </remarks>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The status the caller actually received.</summary>
    public int StatusCode { get; set; }

    /// <summary>How long it took, in milliseconds.</summary>
    public int DurationMs { get; set; }

    /// <summary>When it started. Always UTC.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>The caller's object id, when authenticated. Null is ordinary: 401s and rate-limited requests have none.</summary>
    public string? UserId { get; set; }

    /// <summary>The caller's name as their token spelled it.</summary>
    public string? UserName { get; set; }

    /// <summary>
    /// The transport peer's address, captured before forwarded headers are applied.
    /// </summary>
    /// <remarks>
    /// This one is unforgeable — it is who actually connected. Kept separate from
    /// <see cref="ForwardedIp"/> because the two answer different questions and only one of them
    /// can be trusted.
    /// </remarks>
    public string? PeerIp { get; set; }

    /// <summary>
    /// The address after <c>X-Forwarded-For</c> was applied.
    /// </summary>
    /// <remarks>
    /// Caller-supplied and not verified: this deployment trusts forwarded headers from any peer, so
    /// this value is whatever the client claimed. Stored because behind a real proxy it is the useful
    /// one, and labelled in the UI so nobody reads it as proof.
    /// </remarks>
    public string? ForwardedIp { get; set; }

    /// <summary>The authorization policy the endpoint required, when it declared one.</summary>
    public string? Permission { get; set; }

    /// <summary>The id echoed to the caller and written to the log, so an entry can be joined to its log events.</summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// The CLR type name of an unhandled exception, never its message.
    /// </summary>
    /// <remarks>
    /// A database driver's exception message can contain a connection string. The type is enough to
    /// classify the failure here; the full text stays in the log, reachable by
    /// <see cref="CorrelationId"/>.
    /// </remarks>
    public string? ExceptionType { get; set; }

    /// <summary>
    /// True for failures and for anything that changed state.
    /// </summary>
    /// <remarks>
    /// Persisted rather than derived so the retention query and the default view are one indexed
    /// predicate that cannot drift apart from each other.
    /// </remarks>
    public bool IsNotable { get; set; }
}
