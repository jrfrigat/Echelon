namespace Echelon.Application.DTOs;

/// <summary>The vocabulary of <see cref="TimelineEntryDto.ClockSource"/>.</summary>
public static class TimelineClockSources
{
    /// <summary>Stamped by this service.</summary>
    public const string Ours = "ours";

    /// <summary>
    /// Stamped by the VCS or the tracker, so it is not strictly comparable with our own timestamps.
    /// </summary>
    /// <remarks>
    /// Marked rather than silently mixed: a merge request's creation time means "when GitLab opened
    /// it" on the poll path and "when we received it" on the webhook path, with nothing in the row
    /// to say which. Ordering across the two is approximate and the reader is told so.
    /// </remarks>
    public const string External = "external";
}
