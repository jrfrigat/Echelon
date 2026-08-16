namespace Echelon.Providers.Abstractions;

/// <summary>
/// Canonical form of a provider type key.
/// </summary>
/// <remarks>
/// The key is a string rather than an enum because it arrives from the database
/// (<c>VcsConnection.ProviderType</c>), and an enum would make adding a provider a change to the
/// domain plus a data migration. Strings from an operator, an HTTP request and a database column
/// all have to agree, so every comparison goes through <see cref="Normalize"/> rather than
/// trusting the caller to have matched case.
/// </remarks>
public static class ProviderKey
{
    /// <summary>Trims and lower-cases a key so lookups are case- and whitespace-insensitive.</summary>
    /// <param name="providerType">Raw key, typically from configuration, the API or the database.</param>
    /// <returns>The canonical key, or an empty string when the input is null or blank.</returns>
    public static string Normalize(string? providerType) =>
        providerType?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>Compares two keys in their canonical form.</summary>
    /// <param name="left">First key.</param>
    /// <param name="right">Second key.</param>
    /// <returns><c>true</c> when both normalize to the same value.</returns>
    public static bool Matches(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
}
