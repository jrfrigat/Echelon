namespace Echelon.Application.DTOs;

/// <summary>The installation-wide wait policy.</summary>
/// <param name="WaitForSubtasks">Whether a parent waits for the tasks beneath it.</param>
/// <param name="WaitForLinked">Whether a task waits for the tasks it declares a dependency on.</param>
/// <param name="PrerequisiteGroupOrder">
/// <c>Together</c>, <c>SubtasksFirst</c> or <c>LinkedFirst</c>. See the enum of the same name.
/// </param>
public record PlanningPolicyDto(bool WaitForSubtasks, bool WaitForLinked, string PrerequisiteGroupOrder);
