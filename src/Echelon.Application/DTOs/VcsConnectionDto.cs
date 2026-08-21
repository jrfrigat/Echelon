namespace Echelon.Application.DTOs;

/// <summary>A configured VCS connection. Never carries the access token.</summary>
/// <param name="VcsType">
/// The provider type, e.g. <c>gitlab-webhook</c> or <c>gitlab-poll</c> - this is what carries push
/// versus poll. The wire keeps saying "vcsType" while the column is <c>ProviderType</c>: renaming the
/// field would break every client for no gain.
/// </param>
/// <param name="Id">The connection id.</param>
/// <param name="Name">The connection's name, unique across connections.</param>
/// <param name="ApiUrl">Where the provider is reached.</param>
/// <param name="Settings">
/// Provider-specific settings, keyed as the provider declares them. Secret ones are absent, not
/// masked - a mask would be submitted back as though it were the value.
/// </param>
public record VcsConnectionDto(
    Guid Id,
    string Name,
    string VcsType,
    string ApiUrl,
    IReadOnlyDictionary<string, string>? Settings = null);
