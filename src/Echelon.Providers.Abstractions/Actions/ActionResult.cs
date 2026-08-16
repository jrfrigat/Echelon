namespace Echelon.Providers.Abstractions.Actions;

/// <summary>The outcome of running one action.</summary>
/// <param name="Success">Whether the action succeeded.</param>
/// <param name="Message">An optional detail (a failure reason, an external reference).</param>
public sealed record ActionResult(bool Success, string? Message = null);
