namespace Echelon.Core.Enums;

/// <summary>Which side of the ingestion a connection sits on.</summary>
public enum IngestionConnectionKind
{
    /// <summary>A VCS connection: repositories, merge requests, branches.</summary>
    Vcs = 0,

    /// <summary>A tracker connection: tasks, their statuses and their links.</summary>
    Tracker = 1
}
