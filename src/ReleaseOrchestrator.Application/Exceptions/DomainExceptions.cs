namespace ReleaseOrchestrator.Application.Exceptions;

/// <summary>The requested entity does not exist. Maps to HTTP 404.</summary>
public class NotFoundException(string message) : Exception(message);

/// <summary>The caller's input is invalid or violates a domain rule. Maps to HTTP 400.</summary>
public class DomainValidationException(string message) : Exception(message);
