namespace ReleaseOrchestrator.Application.Contracts.Messages;

/// <summary>
/// Where the messages in this assembly are delivered.
/// </summary>
/// <remarks>
/// One queue, named once. The process that sends (the ingress, the API) and the process that
/// handles (Core) both route to it, and a queue name that drifts between them fails silently — the
/// sender publishes into the void and no handler ever sees it. Keeping the name here, beside the
/// contracts it routes, is what stops the two sides from disagreeing.
/// </remarks>
public static class MessageRouting
{
    /// <summary>The Core process's single input queue.</summary>
    public const string InputQueue = "release-orchestrator";
}
