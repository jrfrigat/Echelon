namespace ReleaseOrchestrator.Application.Contracts.Messages;

public record MrOpened(
    string ConnectionName,
    string RepositoryExternalId,
    string ExternalMrId,
    string SourceBranch,
    string TargetBranch,
    string? TaskExternalId);
