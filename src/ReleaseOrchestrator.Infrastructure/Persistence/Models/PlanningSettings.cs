using System.ComponentModel.DataAnnotations;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// The installation-wide defaults for what a rollout waits for, which any task may override.
/// </summary>
/// <remarks>
/// <para>
/// A single row, identified by <see cref="SingletonId"/>. A settings table rather than configuration
/// because this is an operator decision that changes with how teams use their tracker, not a
/// deployment parameter — and it has to be editable from the admin screens beside the environments
/// and readiness rules it sits with. Nothing here is a secret, so it needs no protection.
/// </para>
/// <para>
/// The defaults reproduce what the planner did before the policy existed — wait for everything,
/// impose no group order — so an upgrade changes no plan until somebody decides otherwise.
/// </para>
/// </remarks>
public class PlanningSettings
{
    /// <summary>
    /// The fixed key of the one row. A constant rather than an arbitrary id so a reader cannot create
    /// a second set of settings that would silently never be applied.
    /// </summary>
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>Primary key; always <see cref="SingletonId"/>.</summary>
    [Key]
    public Guid Id { get; set; } = SingletonId;

    /// <summary>Whether a parent waits for its subtasks, unless the task says otherwise.</summary>
    public bool WaitForSubtasks { get; set; } = true;

    /// <summary>Whether a task waits for the tasks it declares a dependency on, unless it says otherwise.</summary>
    public bool WaitForLinked { get; set; } = true;

    /// <summary>Whether one whole group of prerequisites precedes the other, unless the task says otherwise.</summary>
    public PrerequisiteGroupOrder PrerequisiteGroupOrder { get; set; } = Core.Enums.PrerequisiteGroupOrder.Together;

    /// <summary>Optimistic-concurrency token. See <c>ProviderSpecificMapping</c> for the Postgres mapping.</summary>
    /// <remarks>Nullable like the other tokens: SQL Server generates it, SQLite (used in tests) cannot.</remarks>
    [Timestamp]
    public byte[]? RowVersion { get; set; }
}
