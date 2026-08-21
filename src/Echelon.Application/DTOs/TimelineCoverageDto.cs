namespace Echelon.Application.DTOs;

/// <summary>
/// The limits of what the timeline can say, carried as data rather than left for the reader to guess.
/// </summary>
/// <param name="RecordingBeganAt">
/// The earliest arrival timestamp this service has recorded, used to draw the line before which
/// silence means "not recorded" rather than "nothing happened". Null when nothing has been recorded yet.
/// </param>
/// <param name="Truncated">True when any source hit its row cap, so entries are missing from the middle.</param>
/// <param name="AttributionIsShared">
/// True when the deployment issues one identity to every operator, which makes every "who" on this
/// page the same person regardless of who acted.
/// </param>
/// <remarks>
/// A timeline that renders an incomplete picture as though it were complete is worse than one that
/// refuses to render: the reader draws conclusions from absences. These flags exist so the UI can
/// say which absences are meaningful.
/// </remarks>
public record TimelineCoverageDto(
    DateTime? RecordingBeganAt,
    bool Truncated,
    bool AttributionIsShared);
