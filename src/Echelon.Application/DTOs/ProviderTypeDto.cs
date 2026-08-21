using Echelon.Core.Enums;
using Echelon.Providers.Abstractions;

namespace Echelon.Application.DTOs;
/// <summary>
/// A provider a connection can be backed by: what it is called, what it needs configured, and how its
/// events arrive.
/// </summary>
/// <remarks>
/// The settings are the adapter's own <see cref="ProviderSettingSchema"/>, passed through rather than
/// re-described. The admin form is built from whatever the selected provider declares, so a new
/// provider - or a new setting on an existing one - reaches the UI with no change on either side.
/// </remarks>
/// <param name="ProviderType">The canonical key a connection stores, e.g. <c>gitlab-poll</c>.</param>
/// <param name="Settings">The fields this provider declares, in the order it declares them.</param>
/// <param name="Ingestion">Push or Poll for a connector; null for a deploy strategy, which has neither.</param>
public record ProviderTypeDto(
    string ProviderType,
    IReadOnlyList<ProviderSettingSchema> Settings,
    IngestionMode? Ingestion = null);
