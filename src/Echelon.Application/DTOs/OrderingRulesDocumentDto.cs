namespace Echelon.Application.DTOs;

/// <summary>The ordering-rule document, as text.</summary>
/// <param name="Document">
/// The rules, as YAML. A document written as JSON is also accepted, since JSON is valid YAML - which
/// is what keeps anything stored before the YAML reader existed readable. Empty means no rules.
/// </param>
public record OrderingRulesDocumentDto(string Document);
