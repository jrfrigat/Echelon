using ReleaseOrchestrator.Infrastructure.Actions;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Providers.Abstractions;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Actions;

/// <summary>
/// A setting a handler marks Secret (e.g. a Telegram bot token) must be encrypted at rest in
/// <c>ActionBinding.SettingsJson</c>, the same treatment a connection access token gets -- not stored
/// in plaintext with only a UI mask.
/// </summary>
public class ActionSecretProtectionTests
{
    private static TokenProtector Protector() => ActionDispatcherTests.Protector();

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

        var json = ActionSecretProtection.ProtectForStorage(settings, Schema, protector)!;

        // The secret never appears in cleartext at rest; the non-secret is stored verbatim.
        Assert.DoesNotContain("123456:ABCDEF", json);
        Assert.Contains("42", json);

        var read = ActionSecretProtection.UnprotectForUse(json, Schema, protector);
        Assert.Equal("123456:ABCDEF", read["botToken"]);
        Assert.Equal("42", read["chatId"]);
    }

    [Fact]
    public void NullSettingsStoreAsNull() =>
        Assert.Null(ActionSecretProtection.ProtectForStorage(null, Schema, Protector()));

    [Fact]
    public void AnEmptySecretIsLeftEmpty()
    {
        var protector = Protector();
        var settings = new Dictionary<string, string> { ["botToken"] = "", ["chatId"] = "42" };

        var json = ActionSecretProtection.ProtectForStorage(settings, Schema, protector)!;
        var read = ActionSecretProtection.UnprotectForUse(json, Schema, protector);

        Assert.Equal("", read["botToken"]);
        Assert.Equal("42", read["chatId"]);
    }
}
