namespace Echelon.Application.DTOs;

/// <summary>
/// A mandatory ordering constraint an imported plan does not honour, named the way the document is.
/// </summary>
/// <param name="Kind">Which constraint: a task dependency or a hard repository link.</param>
/// <param name="From">The merge request that should deploy first, by its document key.</param>
/// <param name="To">The merge request that should wait.</param>
/// <remarks>
/// Keys rather than ids: this answers an operator looking at the YAML they just wrote, and a pair of
/// GUIDs would send them back to the database to find out which two lines to change.
/// </remarks>
public record PlanImportViolationDto(string Kind, string From, string To);
