using Echelon.Core.Enums;

namespace Echelon.Application.DTOs;
/// <summary>How one piece of work stands against one environment's readiness rule.</summary>
/// <param name="Status">The judgement, including who made it - a rule or an operator's pin.</param>
/// <param name="IsReady">Whether the gate would let it through.</param>
/// <param name="MissingSignals">What the rule wanted and did not find; empty when nothing is missing.</param>
public record WorkItemReadinessDto(
    WorkItemReadiness Status,
    bool IsReady,
    IReadOnlyList<string> MissingSignals);
