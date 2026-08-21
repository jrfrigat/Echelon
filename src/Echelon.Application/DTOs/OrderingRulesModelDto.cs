namespace Echelon.Application.DTOs;

/// <summary>
/// The ordering rules as a structure the visual editor can hold.
/// </summary>
/// <remarks>
/// Mutable for the same reason as the two types it carries: the form adds and removes groups and rules
/// in place, and a second copy of this shape in the browser is what this design exists to avoid.
/// </remarks>
public sealed record OrderingRulesModelDto
{
    /// <summary>
    /// False when the stored document says something the form cannot express - a wait policy, a
    /// per-task override, a nested exclude. Saving from the form would drop it, so the form refuses to
    /// own it and the text editor stays the way in.
    /// </summary>
    public bool Editable { get; set; }

    /// <summary>Why the stored document could not be read, when it could not.</summary>
    public List<string> Problems { get; set; } = [];

    /// <summary>The named selectors.</summary>
    public List<OrderingRuleGroupDto> Groups { get; set; } = [];

    /// <summary>The ordering between them.</summary>
    public List<OrderingRuleOrderDto> Order { get; set; } = [];
}
