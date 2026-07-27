namespace ReleaseOrchestrator.Providers.Abstractions.Deploy;

/// <summary>
/// Declares that a deploy strategy is registered under <paramref name="Key"/>.
/// </summary>
/// <remarks>
/// Same reason as <see cref="VcsProviderRegistration"/>: keyed DI cannot enumerate its keys, so this
/// is what lets the factory answer "must be one of: gitlab-merge, gitlab-pipeline" and the API
/// validate a strategy key before writing it to a repository.
/// </remarks>
/// <param name="Key">The canonical key, as produced by <see cref="ProviderKey.Normalize"/>.</param>
/// <param name="Description">A short, human sentence for the admin "installed plugins" view; null when none.</param>
public sealed record DeployStrategyRegistration(string Key, string? Description = null);
