using Echelon.Infrastructure.Persistence.Models;
using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Vcs;

namespace Echelon.Infrastructure.Providers;

/// <summary>
/// Asks a repository's VCS which job names its recent pipelines ran.
/// </summary>
/// <remarks>
/// It exists here rather than in the controller for the same reason the pollers do: turning a stored
/// connection into what the provider factory takes is <see cref="ConnectionDescriptors"/>'s job, and
/// that is deliberately internal to this assembly - the API layer is not supposed to know that a
/// database row can supply a provider's credentials.
/// </remarks>
/// <param name="factory">Builds the provider bound to the repository's connection.</param>
public sealed class PipelineJobLookup(IVcsProviderFactory factory)
{
    /// <summary>Lists the job names of the repository's recent pipelines.</summary>
    /// <param name="repository">The repository, with its <see cref="Repository.Connection"/> loaded.</param>
    /// <param name="limit">The most names to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The names; whether the provider can answer this question at all; and the reason the read
    /// failed, if it did. Three outcomes rather than one list, because "this VCS has no CI",
    /// "the token was refused" and "no pipeline has run yet" all produce no names and need different
    /// sentences - an empty picker that cannot say which is which sends the operator looking in the
    /// wrong place.
    /// </returns>
    public async Task<(IReadOnlyList<string> Names, bool Supported, string? Error)> ListAsync(
        Repository repository, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);

        try
        {
            var provider = await factory.CreateAsync(repository.Connection.ToDescriptor(), ct);

            if (provider is not IPipelineJobSource source) return ([], false, null);

            return (await source.ListRecentJobNamesAsync(repository.ExternalId, limit, ct), true, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                   or TaskCanceledException or UnknownProviderException)
        {
            // Reported, not thrown: this fills a picker beside a text box the operator can type into,
            // so a refused token is a sentence to show, not a failed request. UnknownProviderException
            // is in the list because it derives straight from Exception - a connection typed as
            // 'gitab' would otherwise 500 the form rather than name the misspelling.
            return ([], true, ex.Message);
        }
    }
}
