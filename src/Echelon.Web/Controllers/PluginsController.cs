using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Echelon.Infrastructure.Auth;
using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Actions;
using Echelon.Providers.Abstractions.Deploy;

namespace Echelon.Web.Controllers;

/// <summary>
/// The plugins this build has installed - VCS and tracker connectors, deploy strategies and action
/// handlers - so an operator can see at a glance what the deployment can talk to and what each does.
/// </summary>
/// <remarks>
/// Read straight from the marker registrations the composition root added, so the list is exactly what
/// is wired in, and each plugin's own one-line description travels with it. Webhook route details live
/// in each connector's description rather than being resolved here, because the webhook parsers are
/// registered in the ingress host, not this API host.
/// </remarks>
[ApiController]
[Route("api/plugins")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class PluginsController(
    IEnumerable<VcsProviderRegistration> vcs,
    IEnumerable<TrackerProviderRegistration> trackers,
    IEnumerable<DeployStrategyRegistration> deployStrategies,
    IEnumerable<ActionHandlerRegistration> actionHandlers) : ControllerBase
{
    /// <summary>Lists every registered plugin with its category, key, ingestion and description.</summary>
    /// <returns>One entry per installed plugin.</returns>
    /// <remarks>
    /// Trackers report their ingestion just as VCS connectors do. They used to be sent as null here
    /// while declaring one all the same, so <c>yandextracker-poll</c> looked like it had no mode
    /// while <c>gitlab-poll</c> showed one - a difference in this projection, not in the plugins.
    /// </remarks>
    [HttpGet]
    public IActionResult List() => Ok(
        vcs.Select(r => new PluginView("vcs", r.ProviderType, r.Ingestion.ToString(), r.Description))
            .Concat(trackers.Select(r => new PluginView("tracker", r.ProviderType, r.Ingestion.ToString(), r.Description)))
            .Concat(deployStrategies.Select(r => new PluginView("deploy", r.Key, null, r.Description)))
            .Concat(actionHandlers.Select(r => new PluginView("action", r.ActionType, null, r.Description)))
            .OrderBy(p => p.Category, StringComparer.Ordinal)
            .ThenBy(p => p.Key, StringComparer.Ordinal));

    /// <summary>One installed plugin.</summary>
    /// <param name="Category">Which kind: <c>vcs</c>, <c>tracker</c>, <c>deploy</c> or <c>action</c>.</param>
    /// <param name="Key">The registered key (provider type, strategy key, or action type).</param>
    /// <param name="Ingestion">
    /// <c>"Push"</c> or <c>"Poll"</c> for a connector, null for a deploy strategy or action handler,
    /// which have no ingestion at all.
    /// </param>
    /// <remarks>
    /// This is the connector's <em>own</em> declaration, echoed - not a classification this service
    /// makes. Each adapter registers itself with the mode it works in (GitLab registers
    /// <c>gitlab-webhook</c> as Push and <c>gitlab-poll</c> as Poll, in its own extension method), and
    /// the poller then sweeps whichever connections declared Poll. Nothing here inspects a plugin to
    /// decide what it is.
    /// </remarks>
    /// <param name="Description">The plugin's own one-line description; null when it declared none.</param>
    public sealed record PluginView(string Category, string Key, string? Ingestion, string? Description);
}
