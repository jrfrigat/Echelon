namespace Echelon.Application.DTOs;

/// <summary>
/// A named selector, as the visual editor holds it.
/// </summary>
/// <remarks>
/// Settable rather than positional, because this is the one contract an editor binds to directly: the
/// rules form adds and removes globs in place. The alternative - a second, mutable copy of the same
/// shape living in the browser - is exactly the duplication that let a plan version drift.
/// </remarks>
public sealed record OrderingRuleGroupDto
{
    /// <summary>The group name that <c>order</c> entries refer to.</summary>
    public string Name { get; set; } = "";

    /// <summary>Connection-name globs.</summary>
    public List<string> Connectors { get; set; } = [];

    /// <summary>Repository external-id globs.</summary>
    public List<string> Repositories { get; set; } = [];

    /// <summary>Source-branch globs.</summary>
    public List<string> Branches { get; set; } = [];

    /// <summary>Task-key globs.</summary>
    public List<string> TaskKeys { get; set; } = [];

    /// <summary>Labels, matched exactly.</summary>
    public List<string> Labels { get; set; } = [];
}
