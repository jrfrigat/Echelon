namespace Echelon.Providers.Abstractions.Tracker;

/// <summary>
/// A tracker that can enumerate its own open issues, not only answer about one whose key is already
/// known.
/// </summary>
/// <remarks>
/// <para>
/// Optional, and asked with an <c>is</c> check - <c>if (provider is ITrackerIssueSource source)</c> -
/// for the same reason as <see cref="ITrackerDependencySource"/>: whether a tracker can be searched at
/// all is a property of the provider type, and a tracker that cannot would otherwise have to return an
/// empty list, which reads exactly like "nothing is open".
/// </para>
/// <para>
/// This is what makes a poll-mode connection able to start. Polling used to re-read the tasks already
/// in the local database, which never bootstraps: a fresh install has none, so nothing was discovered,
/// the sweep ran over an empty set forever, and a tracker that cannot push webhooks contributed
/// nothing at all. The poller now asks the tracker what is open and re-reads what it already knows,
/// which also catches the issue that was closed while nobody was looking.
/// </para>
/// <para>
/// Keys, not issues, because the key is all the poller needs: every task enters the database through
/// <c>TaskSyncRequested</c> and the one sync path behind it. Returning whole issues here would create a
/// second way for a task to be written, and the two would drift.
/// </para>
/// </remarks>
public interface ITrackerIssueSource
{
    /// <summary>Lists the keys of the issues this connection considers open.</summary>
    /// <param name="limit">The most keys to return; the caller's cap on one sweep.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The keys, oldest-first where the tracker has an order; empty when nothing is open.</returns>
    /// <exception cref="InvalidOperationException">
    /// The connection is not configured for searching - for example, no queue or project was named to
    /// search in. Thrown rather than answered with an empty list, which would look like an empty
    /// tracker and hide the missing setting.
    /// </exception>
    Task<IReadOnlyList<string>> ListOpenIssueKeysAsync(int limit, CancellationToken ct);
}
