namespace Echelon.Core.Enums;

/// <summary>How a connection's events reach the service.</summary>
/// <remarks>
/// Two interchangeable channels that produce the same normalized events, so a consumer cannot tell
/// which one a change arrived through.
/// </remarks>
public enum IngestionMode
{
    /// <summary>The provider pushes events to the webhook front door. The default.</summary>
    Push = 1,

    /// <summary>The service polls the provider on an interval -- for a setup that cannot push webhooks.</summary>
    Poll = 2
}
