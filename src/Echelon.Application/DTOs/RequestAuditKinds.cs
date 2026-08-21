namespace Echelon.Application.DTOs;

/// <summary>The vocabulary of <see cref="RequestAuditRecord.Kind"/>.</summary>
public static class RequestAuditKinds
{
    /// <summary>A call to the API.</summary>
    public const string Api = "Api";

    /// <summary>A sign-in or sign-out call.</summary>
    public const string Auth = "Auth";

    /// <summary>
    /// An API-shaped path that matched no endpoint and fell through to the app shell.
    /// </summary>
    /// <remarks>
    /// Distinguished because the fallback answers 200 for anything, so without this a mistyped or
    /// probed URL would be counted as a successful API call.
    /// </remarks>
    public const string RoutingMiss = "RoutingMiss";

    /// <summary>Anything else that matched an endpoint.</summary>
    public const string Other = "Other";
}
