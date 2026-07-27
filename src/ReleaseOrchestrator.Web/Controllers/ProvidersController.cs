using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Deploy;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;

namespace ReleaseOrchestrator.Web.Controllers;

/// <summary>
/// Which providers this deployment can talk to, and what each one needs configured.
/// </summary>
/// <remarks>
/// This is what lets the admin UI stop naming providers. Before it, the form hard-coded
/// &lt;option value="GitLab"&gt; and an "Org ID" field that only Yandex.Tracker has any use for, so
/// adding a provider meant editing the UI — and the one provider that needed a setting decided
/// the shape of the form for every provider that did not.
///
/// The answer comes from the adapters actually registered in the composition root rather than
/// from a list, so a UI cannot offer a provider the server does not have.
/// </remarks>
[ApiController]
[Route("api/providers")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class ProvidersController(
    IVcsProviderFactory vcsFactory,
    ITrackerProviderFactory trackerFactory,
    IDeployStrategyFactory deployFactory,
    IEnumerable<VcsProviderRegistration> vcsRegistrations) : ControllerBase
{
    /// <summary>Lists the registered VCS providers, their settings, and whether each is push or poll.</summary>
    /// <returns>One entry per provider.</returns>
    [HttpGet("vcs")]
    public IActionResult ListVcsProviders() =>
        Ok(vcsFactory.AvailableProviders
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => Describe(p, vcsFactory.GetSettingsSchema(p), IngestionOf(p))));

    // Push or Poll for a VCS type, from its registration — this is what tells the UI which connections
    // can be polled by hand (a poll type would otherwise wait for its next timer tick).
    private string? IngestionOf(string providerType) =>
        vcsRegistrations
            .FirstOrDefault(r => ProviderKey.Normalize(r.ProviderType) == providerType)
            ?.Ingestion.ToString();

    /// <summary>Lists the registered tracker providers and their settings.</summary>
    /// <returns>One entry per provider.</returns>
    [HttpGet("trackers")]
    public IActionResult ListTrackerProviders() =>
        Ok(trackerFactory.AvailableProviders
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => Describe(p, trackerFactory.GetSettingsSchema(p))));

    /// <summary>
    /// Lists the registered deploy strategies and their settings, so the deploy-target form can offer
    /// them and render each one's fields — the same schema-driven shape the connection forms use.
    /// </summary>
    /// <returns>One entry per strategy, keyed by <c>ProviderType</c> (the strategy key).</returns>
    [HttpGet("deploy-strategies")]
    public IActionResult ListDeployStrategies() =>
        Ok(deployFactory.AvailableStrategies
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => Describe(k, deployFactory.GetSettingsSchema(k))));

    private static object Describe(
        string providerType, IEnumerable<Providers.Abstractions.ProviderSettingSchema> schema,
        string? ingestion = null) =>
        new
        {
            ProviderType = providerType,
            // Push/Poll for VCS providers; null for trackers and deploy strategies, which have no such axis.
            Ingestion = ingestion,
            // Secret settings are declared but their values are never read back — the schema tells
            // the UI to render a write-only field, and nothing here can leak one. Kind/Options/etc.
            // let the UI render a number, a dropdown or a pattern rather than a text box for all.
            Settings = schema.Select(s => new
            {
                s.Key, s.Label, s.Description, s.Required, s.Secret,
                Kind = s.Kind.ToString(), s.Options, s.Default, s.Min, s.Max
            })
        };
}
