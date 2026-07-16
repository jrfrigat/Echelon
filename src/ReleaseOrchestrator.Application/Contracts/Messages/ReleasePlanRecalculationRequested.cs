namespace ReleaseOrchestrator.Application.Contracts.Messages;

public record ReleasePlanRecalculationRequested(DateTime RequestedAt, string? Reason);
