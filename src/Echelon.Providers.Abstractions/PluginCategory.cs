namespace Echelon.Providers.Abstractions;

/// <summary>
/// The four axes a plugin can extend: what the service talks to, and what it does with what it finds.
/// </summary>
/// <remarks>
/// One value per registration type - <see cref="VcsProviderRegistration"/>,
/// <see cref="TrackerProviderRegistration"/>, <see cref="Deploy.DeployStrategyRegistration"/> and
/// <see cref="Actions.ActionHandlerRegistration"/> - so the admin list of installed plugins is grouped
/// by a value both ends know, rather than by a lowercase word each end spelled for itself. Declaration
/// order is display order: what the service reads from, then what it does with it.
/// </remarks>
public enum PluginCategory
{
    /// <summary>A VCS connector: repositories, merge requests and branches.</summary>
    Vcs = 0,

    /// <summary>A tracker connector: tasks, their statuses and their links.</summary>
    Tracker = 1,

    /// <summary>A deploy strategy: how a merge request is actually shipped into an environment.</summary>
    Deploy = 2,

    /// <summary>An action handler: what a rollout step can be told to do.</summary>
    Action = 3
}
