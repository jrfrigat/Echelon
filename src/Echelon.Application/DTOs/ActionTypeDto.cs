using Echelon.Providers.Abstractions;

namespace Echelon.Application.DTOs;
/// <summary>An action handler and the settings it declares.</summary>
/// <remarks>
/// The schema is the same <see cref="ProviderSettingSchema"/> a provider uses, whole: the endpoint
/// used to send five of its ten fields, which the client read into a full one and filled the rest with
/// defaults - so a bounded number rendered as a text box and nobody could see why.
/// </remarks>
/// <param name="ActionType">The handler's registered key.</param>
/// <param name="Settings">The fields it declares, in the order it declares them.</param>
public record ActionTypeDto(string ActionType, IReadOnlyList<ProviderSettingSchema> Settings);
