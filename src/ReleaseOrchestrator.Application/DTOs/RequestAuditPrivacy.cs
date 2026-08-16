namespace ReleaseOrchestrator.Application.DTOs;

/// <summary>
/// Which personally-identifying fields the request audit is allowed to record.
/// </summary>
/// <remarks>
/// <para>
/// A small type of its own rather than the full options class, because the recorder lives in the web
/// host and must not gain a dependency on Infrastructure - it is compiled into the webhook host too,
/// which deliberately has no database reach.
/// </para>
/// <para>
/// It exists at all because the two switches were documented, shipped in <c>appsettings.json</c>, and
/// consulted by nothing. A privacy setting that reads as honoured and is not is worse than no
/// setting: an operator who turns off IP recording to satisfy a policy believes they have.
/// </para>
/// </remarks>
/// <param name="RecordClientIp">When false, neither the transport peer nor the forwarded address is stored.</param>
/// <param name="RecordUserName">When false, the caller's display name is not stored; the object id still is.</param>
public sealed record RequestAuditPrivacy(bool RecordClientIp, bool RecordUserName)
{
    /// <summary>The default when nothing says otherwise, and the fallback when the options are absent.</summary>
    public static RequestAuditPrivacy RecordEverything { get; } = new(RecordClientIp: true, RecordUserName: true);
}
