using ReleaseOrchestrator.Providers.Abstractions.Tracker;
using ReleaseOrchestrator.Providers.YandexTracker;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Providers.YandexTracker;

/// <summary>
/// The closed-status list moved here from Core with its meaning intact: "resolved" belongs in it.
/// Two copies of this list once disagreed on exactly that, which left resolved tasks closed
/// enough to trigger a replan but never archivable.
/// </summary>
public class YandexTrackerStatusRulesTests
{
    [Theory]
    [InlineData("closed")]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    // The one the two lists disagreed about.
    [InlineData("resolved")]
    [InlineData("RESOLVED")]
    [InlineData("  closed  ")]
    public void ClosedStatusesAreClosed(string status)
        => Assert.True(YandexTrackerStatusRules.IsClosed(status));

    [Theory]
    [InlineData("open")]
    [InlineData("inProgress")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElseIsOpen(string? status)
        => Assert.False(YandexTrackerStatusRules.IsClosed(status));
}

/// <summary>
/// The organization id is Yandex.Tracker's alone, so it left the shared port and the shared
/// entity for typed options read out of the connection's settings bag.
/// </summary>
public class YandexTrackerOptionsTests
{
    [Fact]
    public void ReadsTheOrganizationIdFromTheSettingsBag()
        => Assert.Equal("org-42", YandexTrackerOptions.From(Context(("orgId", "org-42"))).OrgId);

    [Fact]
    public void KeyMatchIsCaseInsensitive()
    {
        // The bag round-trips through JSON written by an older release and by the API, so its
        // casing is not something this adapter gets to assume.
        Assert.Equal("org-42", YandexTrackerOptions.From(Context(("OrgId", "org-42"))).OrgId);
    }

    [Fact]
    public void TrimsTheOrganizationId()
        => Assert.Equal("org-42", YandexTrackerOptions.From(Context(("orgId", "  org-42  "))).OrgId);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingOrganizationIdIsAConfigurationErrorNotAnEmptyHeader(string? orgId)
    {
        // The old code passed `OrgId ?? string.Empty`, which sent a blank X-Org-Id and turned a
        // missing setting into an opaque 401 from the tracker.
        var exception = Assert.Throws<InvalidOperationException>(
            () => YandexTrackerOptions.From(Context(("orgId", orgId))));

        Assert.Contains("my-tracker", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orgId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySettingsBagIsAlsoAConfigurationError()
        => Assert.Throws<InvalidOperationException>(() => YandexTrackerOptions.From(Context()));

    private static TrackerProviderContext Context(params (string Key, string? Value)[] settings) =>
        new(
            ConnectionName: "my-tracker",
            ApiUrl: new Uri("https://api.tracker.yandex.net"),
            AccessToken: "token",
            ProviderSettings: settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase));
}
