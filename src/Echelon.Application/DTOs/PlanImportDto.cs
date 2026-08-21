namespace Echelon.Application.DTOs;

/// <summary>What validating or importing a plan document produced.</summary>
/// <param name="Accepted">Whether the document would be (or was) applied.</param>
/// <param name="Errors">Why it cannot be applied at all. Empty when it can.</param>
/// <param name="Violations">
/// Constraints the requested order breaks. Not errors: an operator may deploy against the declared
/// order, which is what <c>force</c> is for - but never without the plan recording it.
/// </param>
/// <param name="Plan">The plan as stored, on a successful import. Null for a validate.</param>
public record PlanImportDto(
    bool Accepted,
    IReadOnlyList<string> Errors,
    IReadOnlyList<PlanImportViolationDto> Violations,
    RolloutPlanDto? Plan);
