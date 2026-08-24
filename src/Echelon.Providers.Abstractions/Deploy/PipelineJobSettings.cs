namespace Echelon.Providers.Abstractions.Deploy;

/// <summary>
/// The neutral spelling of "which CI job is the deploy", for strategies that deploy by running one.
/// </summary>
/// <remarks>
/// Named once, here, for the same reason <c>VcsPollSettings.IntervalKey</c> is: deploying by running
/// a named job is not a GitLab idea, and the admin UI offers a job picker to whichever strategy
/// declares this key. Keeping the spelling private to one provider would force the picker to name
/// that provider - exactly what the schema-driven settings form exists to avoid - or to never appear
/// for the second one.
/// </remarks>
public static class PipelineJobSettings
{
    /// <summary>Settings-bag key naming the job to run. Stable: it is persisted on deploy targets.</summary>
    public const string JobKey = "job";
}
