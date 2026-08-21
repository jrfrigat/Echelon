namespace Echelon.Application.DTOs;

/// <summary>An action bound to an event: what to do, and when.</summary>
/// <param name="Id">The binding id.</param>
/// <param name="EventType">The event that triggers it.</param>
/// <param name="ActionType">The handler that runs.</param>
/// <param name="Scope">What it is limited to, or null for everything.</param>
/// <param name="Order">Where it runs among the bindings for the same event; lower first.</param>
/// <param name="Enabled">Whether it runs at all.</param>
public record ActionBindingDto(
    Guid Id,
    string EventType,
    string ActionType,
    string? Scope,
    int Order,
    bool Enabled);
