using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReleaseOrchestrator.Infrastructure.Auth;
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
    ITrackerProviderFactory trackerFactory) : ControllerBase
{
    /// <summary>Lists the registered VCS providers and their settings.</summary>
    /// <returns>One entry per provider.</returns>
    [HttpGet("vcs")]
    public IActionResult ListVcsProviders() =>
        Ok(vcsFactory.AvailableProviders
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => Describe(p, vcsFactory.GetSettingsSchema(p))));

    /// <summary>Lists the registered tracker providers and their settings.</summary>
    /// <returns>One entry per provider.</returns>
    [HttpGet("trackers")]
    public IActionResult ListTrackerProviders() =>
        Ok(trackerFactory.AvailableProviders
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => Describe(p, trackerFactory.GetSettingsSchema(p))));

    private static object Describe(
        string providerType, IEnumerable<Providers.Abstractions.ProviderSettingSchema> schema) =>
        new
        {
            ProviderType = providerType,
            // Secret settings are declared but their values are never read back — the schema tells
            // the UI to render a write-only field, and nothing here can leak one.
            Settings = schema.Select(s => new { s.Key, s.Label, s.Description, s.Required, s.Secret })
        };
}
