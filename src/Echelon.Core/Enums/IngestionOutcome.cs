namespace Echelon.Core.Enums;

/// <summary>How a worker's last pass ended.</summary>
public enum IngestionOutcome
{
    /// <summary>It has not finished a pass yet on this replica.</summary>
    None = 0,

    /// <summary>It completed.</summary>
    Ok = 1,

    /// <summary>It threw. The message is kept beside this, because "failed" alone is not actionable.</summary>
    Failed = 2
}
