namespace Echelon.Application.DTOs;

/// <summary>A repository a poll could not read, and why.</summary>
/// <param name="Repository">The repository, as configured - usually where the mistake is.</param>
/// <param name="Reason">A human explanation, aimed at that misconfiguration.</param>
public record VcsPollFailureDto(string Repository, string Reason);
