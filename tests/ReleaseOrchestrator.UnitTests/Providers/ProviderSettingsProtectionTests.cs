using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Providers;
using ReleaseOrchestrator.Providers.Abstractions;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Providers;

/// <summary>
/// A setting a schema marks Secret (a bot token, a second credential) must be encrypted at rest in
/// whichever settings column holds it -- an action binding's or a connection's -- and get the same
/// treatment a connection access token gets, not plaintext behind a UI mask.
/// </summary>
public class ProviderSettingsProtectionTests
{
    internal static TokenProtector Protector() =>
        new(new ServiceCollection().AddDataProtection().Services.BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>());

    private static readonly IReadOnlyList<ProviderSettingSchema> Schema =
    [
        new("botToken", "Bot token", Required: true, Secret: true),
        new("chatId", "Chat id", Required: true, Secret: false),
    ];

    [Fact]
    public void ASecretIsNotStoredInPlaintextAndRoundTrips()
    {
        var protector = Protector();
        var settings = new Dictionary<string, string> { ["botToken"] = "123456:ABCDEF", ["chatId"] = "42" };

        var json = ProviderSettingsProtection.ProtectForStorage(settings, Schema, protector)!;

        // The secret never appears in cleartext at rest; the non-secret is stored verbatim.
        Assert.DoesNotContain("123456:ABCDEF", json);
        Assert.Contains("42", json);

        var read = ProviderSettingsProtection.UnprotectForUse(json, Schema, protector);
        Assert.Equal("123456:ABCDEF", read["botToken"]);
        Assert.Equal("42", read["chatId"]);
    }

    [Fact]
    public void NullSettingsStoreAsNull() =>
        Assert.Null(ProviderSettingsProtection.ProtectForStorage(null, Schema, Protector()));

    [Fact]
    public void AnEmptySecretIsLeftEmpty()
    {
        var protector = Protector();
        var settings = new Dictionary<string, string> { ["botToken"] = "", ["chatId"] = "42" };

        var json = ProviderSettingsProtection.ProtectForStorage(settings, Schema, protector)!;
        var read = ProviderSettingsProtection.UnprotectForUse(json, Schema, protector);

        Assert.Equal("", read["botToken"]);
        Assert.Equal("42", read["chatId"]);
    }
}

/// <summary>
/// Covers the rules that decide whether a connection's provider settings are acceptable.
/// </summary>
/// <remarks>
/// The point of this file is that no rule here names a setting. Before this validator existed, one
/// provider's <c>orgId</c> was spelled out in the entity, the API contract and the admin form, so
/// the single provider that needed a setting dictated the shape for every provider that did not.
/// </remarks>
public class ProviderSettingsBagTests
{
    private static readonly IReadOnlyList<ProviderSettingSchema> Schema =
    [
        new("orgId", "Organization ID", Required: true),
        new("region", "Region")
    ];

    private static Dictionary<string, string?> Submit(params (string Key, string? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    [Fact]
    public void ADeclaredSettingIsAcceptedAndTrimmed()
    {
        var result = ProviderSettingsBag.Validate(
            Submit(("orgId", "  acme  ")), Schema, out var normalized, out _);

        Assert.Equal(ProviderSettingsError.None, result);
        Assert.Equal("acme", normalized["orgId"]);
    }

    /// <summary>
    /// An undeclared key is refused, and the response names it.
    /// </summary>
    /// <remarks>
    /// Stored-and-ignored is the tempting alternative and the worse one: the operator would then be
    /// told the setting is missing while looking straight at the form where they typed it, with no
    /// hint that the key is misspelled.
    /// </remarks>
    [Fact]
    public void AnUndeclaredSettingIsRefusedByName()
    {
        var result = ProviderSettingsBag.Validate(
            Submit(("orgId", "acme"), ("orgID", "acme")), Schema, out _, out var key);

        Assert.Equal(ProviderSettingsError.UnknownKey, result);
        Assert.Equal("orgID", key);
    }

    [Fact]
    public void ARequiredSettingMustBePresent()
    {
        var result = ProviderSettingsBag.Validate(
            Submit(("region", "ru-central1")), Schema, out _, out var key);

        Assert.Equal(ProviderSettingsError.MissingRequired, result);
        Assert.Equal("orgId", key);
    }

    /// <summary>Whitespace does not satisfy a required setting; it is the same as leaving it out.</summary>
    [Fact]
    public void WhitespaceDoesNotSatisfyARequiredSetting()
    {
        var result = ProviderSettingsBag.Validate(
            Submit(("orgId", "   ")), Schema, out _, out var key);

        Assert.Equal(ProviderSettingsError.MissingRequired, result);
        Assert.Equal("orgId", key);
    }

    /// <summary>
    /// Blank and absent collapse to one stored representation, so "cleared" and "never set" cannot
    /// drift into two states that compare unequal but mean the same thing.
    /// </summary>
    [Fact]
    public void ABlankOptionalSettingIsNotStored()
    {
        ProviderSettingsBag.Validate(
            Submit(("orgId", "acme"), ("region", "")), Schema, out var normalized, out _);

        Assert.False(normalized.ContainsKey("region"));
    }

    /// <summary>A provider that declares nothing accepts nothing, and stores null rather than <c>{}</c>.</summary>
    [Fact]
    public void AProviderWithNoSchemaAcceptsAnEmptyBagOnly()
    {
        Assert.Equal(
            ProviderSettingsError.None,
            ProviderSettingsBag.Validate(null, [], out var normalized, out _));
        Assert.Null(ProviderSettingsBag.Serialize(normalized));

        Assert.Equal(
            ProviderSettingsError.UnknownKey,
            ProviderSettingsBag.Validate(Submit(("anything", "x")), [], out _, out _));
    }

    [Fact]
    public void ABagTooLargeForItsColumnIsRefused()
    {
        var result = ProviderSettingsBag.Validate(
            Submit(("orgId", new string('x', ProviderSettingsBag.MaxJsonLength + 1))),
            Schema, out _, out _);

        Assert.Equal(ProviderSettingsError.TooLong, result);
    }

    /// <summary>
    /// A malformed column reads as empty rather than throwing, because the callers are listings and
    /// hiding the row would hide the only thing an operator could act on.
    /// </summary>
    [Fact]
    public void AMalformedColumnReadsAsEmptyButReportsItself()
    {
        Assert.Empty(ProviderSettingsBag.Deserialize("not json"));
        Assert.False(ProviderSettingsBag.TryDeserialize("not json", out _));
        Assert.True(ProviderSettingsBag.TryDeserialize(null, out var empty));
        Assert.Empty(empty);
    }

    [Fact]
    public void RoundTripsThroughStorage()
    {
        ProviderSettingsBag.Validate(Submit(("orgId", "acme")), Schema, out var normalized, out _);

        Assert.Equal(normalized, ProviderSettingsBag.Deserialize(ProviderSettingsBag.Serialize(normalized)));
    }
}
