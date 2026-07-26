using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Providers.Abstractions.Ingestion;

namespace ReleaseOrchestrator.Ingress.Webhooks.Endpoints;

/// <summary>
/// Maps a provider's normalized <see cref="IngestionEvent"/> to the Rebus message that carries it to
/// Core.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place the neutral events meet the wire contract, and it lives in the host on
/// purpose: a provider must not reference <c>Application</c> (that would be a build cycle) or Rebus,
/// so the provider returns neutral events and the host turns them into messages. Keeping the wire
/// messages here also keeps their assembly-qualified type name — and therefore their Rebus wire
/// identity — unchanged.
/// </para>
/// <para>
/// The host supplies the three things a pure parser cannot: the canonical <c>Source</c>
/// (<c>"{sourcePrefix}/{connectionName}"</c>), the connection name, and the receiver's clock for the
/// timestamps the payloads do not carry.
/// </para>
/// </remarks>
public static class IngestionEventMapper
{
    /// <summary>Turns one normalized event into its wire message.</summary>
    /// <param name="ev">The event a parser produced.</param>
    /// <param name="connectionName">The connection the delivery named.</param>
    /// <param name="source">The producer identity, <c>"{sourcePrefix}/{connectionName}"</c>.</param>
    /// <param name="clock">The receiver's clock, for the times the payload did not include.</param>
    /// <returns>The message to send.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The event is a kind this mapper does not know.</exception>
    public static IMessage ToMessage(
        IngestionEvent ev, string connectionName, string source, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(clock);

        return ev switch
        {
            MrOpenedEvent e => new MrOpened(
                ConnectionName: connectionName,
                RepositoryExternalId: e.RepositoryExternalId,
                ExternalMrId: e.ExternalMrId,
                SourceBranch: e.SourceBranch,
                TargetBranch: e.TargetBranch,
                Title: e.Title,
                Labels: e.Labels,
                PipelineResult: e.PipelineResult,
                Source: source,
                EventId: e.ProviderEventId),

            // Labels ride along so a merge event can record the final set for the readiness gate.
            // ChangedAt is the receiver's clock, exactly as the webhook front door has always
            // stamped it — the payload carries no reliable event time.
            MrStatusChangedEvent e => new MrStatusChanged(
                ConnectionName: connectionName,
                RepositoryExternalId: e.RepositoryExternalId,
                ExternalMrId: e.ExternalMrId,
                NewStatus: e.NewStatus,
                ChangedAt: clock.GetUtcNow().UtcDateTime,
                Labels: e.Labels,
                Source: source,
                EventId: e.ProviderEventId),

            TaskCreatedEvent e => new TaskCreated(
                TrackerConnectionName: connectionName,
                ExternalId: e.ExternalId,
                Title: e.Title,
                Source: source,
                EventId: e.ProviderEventId),

            TaskStatusChangedEvent e => new TaskStatusChanged(
                TrackerConnectionName: connectionName,
                ExternalId: e.ExternalId,
                NewStatus: e.NewStatus,
                ClosedAt: e.Closed ? clock.GetUtcNow().UtcDateTime : null,
                Source: source,
                EventId: e.ProviderEventId),

            _ => throw new ArgumentOutOfRangeException(
                nameof(ev), ev.GetType().Name, "No wire message is mapped for this ingestion event.")
        };
    }
}
