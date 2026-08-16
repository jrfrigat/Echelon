using Microsoft.EntityFrameworkCore;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Providers;
using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Actions;
using Echelon.Providers.Abstractions.Tracker;

namespace Echelon.Infrastructure.Actions;

/// <summary>
/// Shared plumbing for actions that write to a tracker: resolve the bound tracker for the event's
/// issue and probe it for the write capability.
/// </summary>
internal static class TrackerActionSupport
{
    /// <summary>Resolves the tracker mutator for the event's issue, or a reason it could not.</summary>
    public static async Task<(ITrackerMutator? Mutator, string? Issue, string? Error)> ResolveAsync(
        ActionContext context, AppDbContext db, ITrackerProviderFactory factory, CancellationToken ct)
    {
        var issueKey = context.Payload.GetValueOrDefault("task");
        var connectionName = context.Payload.GetValueOrDefault("trackerConnectionName");
        if (string.IsNullOrEmpty(issueKey) || string.IsNullOrEmpty(connectionName))
            return (null, null, "The event carries no task or tracker connection.");

        var conn = await db.TrackerConnections.FirstOrDefaultAsync(c => c.Name == connectionName, ct).ConfigureAwait(false);
        if (conn is null)
            return (null, issueKey, $"Tracker connection '{connectionName}' not found.");

        var provider = await factory.CreateAsync(conn.ToDescriptor(), ct).ConfigureAwait(false);
        return provider is ITrackerMutator mutator
            ? (mutator, issueKey, null)
            : (null, issueKey, $"Tracker '{connectionName}' does not support writes.");
    }
}

/// <summary>Moves the event's issue to a configured status.</summary>
/// <remarks>Does nothing for a read-only tracker (the mutator capability is probed, not assumed).</remarks>
internal sealed class TrackerStatusActionHandler(AppDbContext db, ITrackerProviderFactory factory) : IActionHandler
{
    /// <summary>The action-type key this handler is registered under.</summary>
    public const string ActionType = "tracker-status";

    /// <inheritdoc/>
    public IReadOnlyList<ProviderSettingSchema> SettingsSchema =>
    [
        new ProviderSettingSchema("status", "Target status", "The status to move the issue to, in the tracker's own vocabulary.", Required: true)
    ];

    /// <inheritdoc/>
    public async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken ct)
    {
        var (mutator, issue, error) = await TrackerActionSupport.ResolveAsync(context, db, factory, ct).ConfigureAwait(false);
        if (mutator is null || issue is null) return new ActionResult(false, error);

        if (!context.Settings.TryGetValue("status", out var status) || string.IsNullOrWhiteSpace(status))
            return new ActionResult(false, "The 'status' setting is required.");

        await mutator.SetStatusAsync(issue, status, ct).ConfigureAwait(false);
        return new ActionResult(true);
    }
}

/// <summary>Adds a comment to the event's issue.</summary>
internal sealed class TrackerCommentActionHandler(AppDbContext db, ITrackerProviderFactory factory) : IActionHandler
{
    /// <summary>The action-type key this handler is registered under.</summary>
    public const string ActionType = "tracker-comment";

    /// <inheritdoc/>
    public IReadOnlyList<ProviderSettingSchema> SettingsSchema =>
    [
        new ProviderSettingSchema("template", "Comment template",
            "Placeholders: {task}, {environment}, {status}, {event}. Defaults to a rollout summary.", Required: false)
    ];

    /// <inheritdoc/>
    public async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken ct)
    {
        var (mutator, issue, error) = await TrackerActionSupport.ResolveAsync(context, db, factory, ct).ConfigureAwait(false);
        if (mutator is null || issue is null) return new ActionResult(false, error);

        var template = context.Settings.TryGetValue("template", out var t) && !string.IsNullOrWhiteSpace(t)
            ? t
            : "Rollout {task} -> {environment}: {status}";
        var comment = template
            .Replace("{task}", context.Payload.GetValueOrDefault("task", ""), StringComparison.Ordinal)
            .Replace("{environment}", context.Payload.GetValueOrDefault("environment", ""), StringComparison.Ordinal)
            .Replace("{status}", context.Payload.GetValueOrDefault("status", ""), StringComparison.Ordinal)
            .Replace("{event}", context.EventType, StringComparison.Ordinal);

        await mutator.AddCommentAsync(issue, comment, ct).ConfigureAwait(false);
        return new ActionResult(true);
    }
}
