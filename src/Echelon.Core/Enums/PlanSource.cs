namespace Echelon.Core.Enums;

/// <summary>How a per-task rollout plan came to exist. An imported or edited plan outranks a generated one.</summary>
public enum PlanSource
{
    /// <summary>Built by the planner from the atlas.</summary>
    Generated = 1,

    /// <summary>Imported wholesale from YAML.</summary>
    Imported = 2,

    /// <summary>Generated, then adjusted by an operator (recorded as overrides).</summary>
    Edited = 3
}
