using Echelon.Core.Enums;

namespace Echelon.Application.DTOs;
/// <summary>A named readiness rule: the signals a merge request must carry to pass a gate.</summary>
/// <param name="Id">The rule id.</param>
/// <param name="Name">Its name, as an operator chose it.</param>
/// <param name="Mode">Whether every signal is required, or any one of them.</param>
/// <param name="RequiredSignals">The signal tokens, e.g. <c>label:ready-for-prod</c>, <c>mr-status:merged</c>.</param>
public record ReadinessRuleDto(
    Guid Id,
    string Name,
    ReadyRule Mode,
    IReadOnlyList<string> RequiredSignals);
