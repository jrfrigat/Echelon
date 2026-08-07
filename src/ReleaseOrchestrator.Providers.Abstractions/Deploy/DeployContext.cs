namespace ReleaseOrchestrator.Providers.Abstractions.Deploy;

/// <summary>
/// Everything a deploy strategy needs to deploy one merge request into one environment.
/// </summary>
/// <remarks>
/// The credentials come decrypted from the executor (which owns the data-protection stack), the
/// same division as the VCS provider seam. A deploy strategy is chosen per repository
/// (<c>Repository.DeployStrategyKey</c>), independent of the connection's provider type, and the
/// environment is a runtime parameter -- one strategy serves every environment
///.
/// </remarks>
/// <param name="ApiUrl">Base address of the provider's API.</param>
/// <param name="AccessToken">The decrypted access token for the connection.</param>
/// <param name="ConnectionName">The connection's name, for logs and errors.</param>
/// <param name="ProjectPath">The repository, as the provider identifies it.</param>
/// <param name="MergeRequestExternalId">The merge request's provider-scoped id (iid).</param>
/// <param name="EnvironmentKey">The target environment, e.g. <c>staging</c> or <c>prod</c>.</param>
/// <param name="Settings">Strategy-specific settings (schema-declared), addressable per environment.</param>
public sealed record DeployContext(
    Uri ApiUrl,
    string AccessToken,
    string ConnectionName,
    string ProjectPath,
    string MergeRequestExternalId,
    string EnvironmentKey,
    IReadOnlyDictionary<string, string> Settings);
