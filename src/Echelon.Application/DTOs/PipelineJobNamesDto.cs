namespace Echelon.Application.DTOs;

/// <summary>
/// The job names a repository's recent pipelines actually ran, for the deploy-target form.
/// </summary>
/// <remarks>
/// The failure travels with the answer rather than as a status code, for the same reason a poll's
/// does: an empty picker beside a text box is not an error the operator needs stopping for, but "the
/// token cannot read pipelines" is something they need told - and a 500 here would read as though the
/// form itself were broken.
/// </remarks>
/// <param name="Names">The distinct job names, sorted; empty when there are none or none could be read.</param>
/// <param name="Failure">Why the list is empty, when it is empty for a reason; null when the read succeeded.</param>
public sealed record PipelineJobNamesDto(IReadOnlyList<string> Names, string? Failure);
