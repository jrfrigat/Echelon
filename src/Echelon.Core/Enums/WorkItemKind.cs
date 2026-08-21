namespace Echelon.Core.Enums;

/// <summary>
/// What a piece of outstanding work currently rides in.
/// </summary>
/// <remarks>
/// A task's work in a repository exists before anyone raises a merge request for it: the branch is
/// already there, and the work list has to show it or a release engineer cannot see what is coming.
/// The two are the same row with a different carrier, which is what this names.
/// </remarks>
public enum WorkItemKind
{
    /// <summary>A merge request carries the work; its status and signals can be judged.</summary>
    MergeRequest = 0,

    /// <summary>Only a branch exists so far - work in progress that nothing has raised for review.</summary>
    Branch = 1
}
