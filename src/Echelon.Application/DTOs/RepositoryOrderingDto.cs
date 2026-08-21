using Echelon.Core.Enums;

namespace Echelon.Application.DTOs;
/// <summary>One repository-ordering rule: <paramref name="FromRepositoryName"/> deploys after <paramref name="ToRepositoryName"/>.</summary>
/// <param name="Id">The rule id.</param>
/// <param name="FromRepositoryId">The repository that waits.</param>
/// <param name="FromRepositoryName">Its name.</param>
/// <param name="ToRepositoryId">The repository waited on.</param>
/// <param name="ToRepositoryName">Its name.</param>
/// <param name="Type">Whether the rule is hard or advisory.</param>
public record RepositoryOrderingDto(
    Guid Id,
    Guid FromRepositoryId,
    string FromRepositoryName,
    Guid ToRepositoryId,
    string ToRepositoryName,
    StackDependencyType Type);
