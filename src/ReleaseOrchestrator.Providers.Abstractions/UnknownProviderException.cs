namespace ReleaseOrchestrator.Providers.Abstractions;

/// <summary>
/// Thrown when a connection names a provider type that no adapter is registered for.
/// </summary>
/// <remarks>
/// The message lists what is registered. A provider type is stored per connection, so this fires
/// on a row an operator typed — possibly long after the deployment — and "provider 'gitab' is not
/// registered" is only actionable next to "must be one of: gitlab". Renovate's
/// <c>setPlatformApi</c> throws the same shape of error for the same reason.
/// </remarks>
public class UnknownProviderException : Exception
{
    /// <summary>Creates an exception naming the missing provider and the registered alternatives.</summary>
    /// <param name="kind">The port that was being resolved, e.g. <c>VCS</c> or <c>tracker</c>.</param>
    /// <param name="providerType">The provider type that could not be resolved.</param>
    /// <param name="available">The provider types that are registered.</param>
    public UnknownProviderException(string kind, string? providerType, IEnumerable<string> available)
        : base(BuildMessage(kind, providerType, available))
    {
    }

    /// <summary>Creates an exception with a caller-supplied message.</summary>
    /// <param name="message">The message.</param>
    public UnknownProviderException(string message) : base(message)
    {
    }

    /// <summary>Creates an exception with a caller-supplied message and inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public UnknownProviderException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Creates an exception with no message.</summary>
    public UnknownProviderException()
    {
    }

    private static string BuildMessage(string kind, string? providerType, IEnumerable<string> available)
    {
        var registered = available is null ? [] : available.OrderBy(p => p, StringComparer.Ordinal).ToList();

        return registered.Count == 0
            ? $"No {kind} provider is registered, so connection provider type '{providerType}' cannot be resolved. "
              + "Register one in the composition root."
            : $"{kind} provider '{providerType}' is not registered. Must be one of: {string.Join(", ", registered)}.";
    }
}
