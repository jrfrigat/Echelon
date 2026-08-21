using Echelon.Providers.Abstractions.Tracker;
using Echelon.Providers.YandexTracker;
using Xunit;

namespace Echelon.UnitTests.Providers;

/// <summary>
/// What a polled Yandex.Tracker connection actually searches for.
/// </summary>
/// <remarks>
/// The query is the whole reason a poll finds anything, and it is built from settings an operator
/// types. A connection that names neither a queue nor a query cannot be searched at all - and that has
/// to be said out loud, because the alternative (an empty result) is indistinguishable from a tracker
/// with nothing open, which is exactly how a poll-mode connection used to look while doing nothing.
/// </remarks>
public class YandexTrackerSearchQueryTests
{
    private static YandexTrackerOptions Options(string? queues = null, string? query = null)
    {
        var settings = new Dictionary<string, string?> { [YandexTrackerOptions.OrgIdKey] = "org-1" };
        if (queues is not null) settings[YandexTrackerOptions.QueuesKey] = queues;
        if (query is not null) settings[YandexTrackerOptions.SearchQueryKey] = query;

        return YandexTrackerOptions.From(new TrackerProviderContext(
            ConnectionName: "tracker",
            ApiUrl: new Uri("https://api.tracker.yandex.net"),
            AccessToken: "token",
            ProviderSettings: settings));
    }

    [Fact]
    public void QueuesBecomeAQueryForEverythingUnresolvedInThem()
    {
        Assert.Equal(
            "Queue: ECH, OPS AND Resolution: empty()",
            Options(queues: "ECH, OPS").BuildSearchQuery("tracker"));
    }

    [Fact]
    public void AHandWrittenQueryWinsOutright()
    {
        // A workflow whose open states a resolution filter does not describe: the operator's query is
        // used as written, not merged with a guess.
        Assert.Equal(
            "Queue: ECH AND Status: inProgress",
            Options(queues: "ECH", query: "Queue: ECH AND Status: inProgress").BuildSearchQuery("tracker"));
    }

    [Fact]
    public void NamingNeitherIsRefusedWithBothSettingsInTheMessage()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Options().BuildSearchQuery("tracker"));

        Assert.Contains("tracker", error.Message, StringComparison.Ordinal);
        Assert.Contains(YandexTrackerOptions.QueuesKey, error.Message, StringComparison.Ordinal);
        Assert.Contains(YandexTrackerOptions.SearchQueryKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWebhookConnectionNeedsNeitherSettingToConnect()
    {
        // Both keys are absent on a webhook connection, which must still bind: the search settings are
        // only read when a search actually happens.
        var options = Options();

        Assert.Empty(options.Queues);
        Assert.Null(options.SearchQuery);
    }
}
