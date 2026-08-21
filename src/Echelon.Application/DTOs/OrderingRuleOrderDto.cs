namespace Echelon.Application.DTOs;

/// <summary>
/// One ordering rule, as the visual editor holds it.
/// </summary>
/// <remarks>
/// Settable for the same reason as <see cref="OrderingRuleGroupDto"/>: the form edits it in place.
/// <see cref="Type"/> and <see cref="Scope"/> stay strings here because the document's own language
/// spells them, and the renderer reads them back with the same tolerance a hand-written document gets.
/// </remarks>
public sealed record OrderingRuleOrderDto
{
    /// <summary>The group that waits.</summary>
    public string Group { get; set; } = "";

    /// <summary>The groups it waits for.</summary>
    public List<string> Needs { get; set; } = [];

    /// <summary>"Hard" or "Soft".</summary>
    public string Type { get; set; } = "Hard";

    /// <summary>"AcrossPlan" or "WithinTask".</summary>
    public string Scope { get; set; } = "AcrossPlan";
}
