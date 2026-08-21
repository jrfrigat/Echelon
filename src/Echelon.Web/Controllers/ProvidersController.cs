using Echelon.Application.DTOs;
using Echelon.Core.Enums;
using Echelon.Infrastructure.Auth;
using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Deploy;
using Echelon.Providers.Abstractions.Tracker;
using Echelon.Providers.Abstractions.Vcs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Echelon.Web.Controllers;

/// <summary>
/// Which providers this deployment can talk to, and what each one needs configured.
/// </summary>
/// <remarks>
/// This is what lets the admin UI stop naming providers. Before it, the form hard-coded
/// &lt;option value="GitLab"&gt; and an "Org ID" field that only Yandex.Tracker has any use for, so
/// adding a provider meant editing the UI - and the one provider that needed a setting decided
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
    IEnumerable<VcsProviderRegistration> vcsRegistrations,
    IEnumerable<TrackerProviderRegistration> trackerRegistrations) : ControllerBase
{
    /// <summary>Lists the registered VCS providers, their settings, and whether each is push or poll.</summary>
    /// <returns>One entry per provider.</returns>
    [HttpGet("vcs")]
    public IActionResult ListVcsProviders() =>
        Ok(vcsFactory.AvailableProviders
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => Describe(p, vcsFactory.GetSettingsSchema(p), VcsIngestionOf(p))));

    // Push or Poll for a provider type, from its registration - this is what tells the UI which
    // connections can be polled by hand (a poll type would otherwise wait for its next timer tick).
    // The enum itself, not its name: the API serializes enums as their names anyway, so the wire
    // format is unchanged and both ends compare a value instead of a spelling.
    private IngestionMode? VcsIngestionOf(string providerType) =>
        vcsRegistrations
            .FirstOrDefault(r => ProviderKey.Normalize(r.ProviderType) == providerType)
            ?.Ingestion;

    // The tracker half of the same question. A poll-mode tracker is the one that has to be asked what
    // is open, so this is what puts the poll button on a tracker connection's row.
    private IngestionMode? TrackerIngestionOf(string providerType) =>
        trackerRegistrations
            .FirstOrDefault(r => ProviderKey.Normalize(r.ProviderType) == providerType)
            ?.Ingestion;

    /// <summary>Lists the registered tracker providers and their settings.</summary>
    /// <returns>One entry per provider.</returns>
    [HttpGet("trackers")]
    public IActionResult ListTrackerProviders() =>
        Ok(trackerFactory.AvailableProviders
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => Describe(p, trackerFactory.GetSettingsSchema(p), TrackerIngestionOf(p))));

    /// <summary>
    /// Lists the registered deploy strategies and their settings, so the deploy-target form can offer
    /// them and render each one's fields - the same schema-driven shape the connection forms use.
    /// </summary>
    /// <returns>One entry per strategy, keyed by <c>ProviderType</c> (the strategy key).</returns>
    [HttpGet("deploy-strategies")]
    public IActionResult ListDeployStrategies() =>
        Ok(deployFactory.AvailableStrategies
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => Describe(k, deployFactory.GetSettingsSchema(k))));

    /// <summary>One provider as the admin forms need it: its key, its fields, and how its events arrive.</summary>
    /// <remarks>
    /// The adapter's own schema is passed through rather than copied field by field. Secret settings are
    /// declared but never carry a value - the schema tells the UI to render a write-only field, and
    /// nothing here can leak one.
    /// </remarks>
    private static ProviderTypeDto Describe(
        string providerType,
        IReadOnlyList<ProviderSettingSchema> schema,
        IngestionMode? ingestion = null) =>
        new(providerType, schema, ingestion);
}
