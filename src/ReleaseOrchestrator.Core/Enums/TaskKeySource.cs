namespace ReleaseOrchestrator.Core.Enums;

/// <summary>Where a merge request's task key is read from.</summary>
/// <remarks>
/// The link between a merge request and the task it belongs to is not a fixed convention - one team
/// puts the key in the branch name, another in the title, another in a label - so the source is
/// configured per connection rather than assumed. This is that choice.
/// </remarks>
public enum TaskKeySource
{
    /// <summary>Read the key from the source branch name, e.g. <c>feature/PROJ-12-thing</c>.</summary>
    Branch = 1,

    /// <summary>Read the key from the merge request title.</summary>
    Title = 2,

    /// <summary>Read the key from the merge request's labels, taking the first that matches.</summary>
    Label = 3
}
