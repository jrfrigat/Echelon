using Microsoft.Extensions.Localization;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Providers;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Web.Resources;
using ReleaseOrchestrator.Web.Validation;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Providers;

/// <summary>
/// Covers what an update does to settings the operator did not retype.
/// </summary>
/// <remarks>
/// This is the file for one rule, because it is the rule this codebase has already got wrong twice
/// on other fields: a blank secret means <b>keep</b>, and a blank anything-else means <b>clear</b>.
/// Getting the first half wrong deletes a working credential during an unrelated rename — the exact
/// shape of the access-token bug (blank encrypted over a live token) and the ingestion-mode bug
/// (blank silently switched a polling connection back to webhooks). Both presented as "it just
/// stopped working" with an edit history that mentioned neither.
/// </remarks>
public class ProviderSettingsBinderTests
{
    private static readonly IReadOnlyList<ProviderSettingSchema> Schema =
    [
        new("orgId", "Organization ID", Required: true),
        new("appSecret", "App secret", Required: true, Secret: true)
    ];

    private static TokenProtector Protector() => ProviderSettingsProtectionTests.Protector();

    private static Dictionary<string, string?> Submit(params (string Key, string? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static string Stored(TokenProtector protector, string orgId, string secret) =>
        ProviderSettingsProtection.ProtectForStorage(
            new Dictionary<string, string> { ["orgId"] = orgId, ["appSecret"] = secret },
            Schema, protector)!;

    private static bool Bind(
        Dictionary<string, string?>? submitted, string? existingJson, TokenProtector protector,
        out string? json, out string error) =>
        ProviderSettingsBinder.TryBind(
            submitted, Schema, existingJson, protector, new StubLocalizer(), out json, out error);

    /// <summary>An update that leaves the secret box empty keeps the stored secret.</summary>
    [Fact]
    public void ABlankSecretKeepsTheStoredOne()
    {
        var protector = Protector();
        var existing = Stored(protector, "acme", "s3cret");

        Assert.True(Bind(Submit(("orgId", "acme"), ("appSecret", "")), existing, protector, out var json, out _));

        var saved = ProviderSettingsProtection.UnprotectForUse(json, Schema, protector);
        Assert.Equal("s3cret", saved["appSecret"]);
    }

    /// <summary>
    /// Omitting the key entirely is the same as sending it blank — a form that renders no box for an
    /// already-set secret must not be a way to delete it.
    /// </summary>
    [Fact]
    public void AnOmittedSecretKeepsTheStoredOne()
    {
        var protector = Protector();
        var existing = Stored(protector, "acme", "s3cret");

        Assert.True(Bind(Submit(("orgId", "acme")), existing, protector, out var json, out _));

        Assert.Equal("s3cret", ProviderSettingsProtection.UnprotectForUse(json, Schema, protector)["appSecret"]);
    }

    [Fact]
    public void ASuppliedSecretReplacesTheStoredOne()
    {
        var protector = Protector();
        var existing = Stored(protector, "acme", "s3cret");

        Assert.True(Bind(Submit(("orgId", "acme"), ("appSecret", "rotated")), existing, protector, out var json, out _));

        Assert.Equal("rotated", ProviderSettingsProtection.UnprotectForUse(json, Schema, protector)["appSecret"]);
    }

    /// <summary>
    /// The other half of the rule: a non-secret left blank IS cleared, because the form showed its
    /// value, so an empty box is a deliberate erasure. Here that makes the save fail, since the
    /// setting is required — which is the correct report, not a silent revert to the old value.
    /// </summary>
    [Fact]
    public void ABlankNonSecretIsClearedRatherThanKept()
    {
        var protector = Protector();
        var existing = Stored(protector, "acme", "s3cret");

        Assert.False(Bind(Submit(("orgId", ""), ("appSecret", "")), existing, protector, out _, out var error));
        Assert.Equal("Provider_MissingSetting", error);
    }

    /// <summary>
    /// A kept secret satisfies its Required rule. Validating the raw submission instead of the
    /// merged result would reject every update where the operator did not retype the credential.
    /// </summary>
    [Fact]
    public void AKeptSecretSatisfiesRequired()
    {
        var protector = Protector();

        Assert.True(Bind(Submit(("orgId", "acme")), Stored(protector, "acme", "s3cret"), protector, out _, out _));

        // ...and with nothing stored to keep, the same submission is refused.
        Assert.False(Bind(Submit(("orgId", "acme")), existingJson: null, protector, out _, out var error));
        Assert.Equal("Provider_MissingSetting", error);
    }

    [Fact]
    public void AnUndeclaredSettingIsRefused()
    {
        Assert.False(Bind(
            Submit(("orgId", "acme"), ("appSecret", "s"), ("nope", "x")),
            existingJson: null, Protector(), out _, out var error));

        Assert.Equal("Provider_UnknownSetting", error);
    }

    /// <summary>
    /// What the API hands back must never contain a secret, not even masked: a placeholder would be
    /// submitted back as though it were the value, and the next save would store the mask.
    /// </summary>
    [Fact]
    public void DisplayWithholdsSecretsEntirely()
    {
        var protector = Protector();

        var shown = ProviderSettingsBinder.ReadForDisplay(Stored(protector, "acme", "s3cret"), Schema);

        Assert.Equal("acme", shown["orgId"]);
        Assert.False(shown.ContainsKey("appSecret"));
    }

    /// <summary>Returns the key rather than a translation, so assertions name the rule that fired.</summary>
    private sealed class StubLocalizer : IStringLocalizer<ApiStrings>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
