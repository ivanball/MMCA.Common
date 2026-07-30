namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// Reference-counts live channel membership for <see cref="NotificationHubService"/>.
/// <para>
/// Set semantics are wrong for this: an invisible listener and a page can hold the same channel at
/// the same time, and with a set the first leaver removes the only entry and cuts the channel off
/// for every other subscriber in the circuit. Counting joins per key means the server is told to
/// join on the first join and to leave only on the last matching leave.
/// </para>
/// <para>
/// Self-synchronized, because joins and leaves arrive from component lifecycle callbacks on
/// different render batches.
/// </para>
/// </summary>
internal sealed class ChannelReferenceCounter
{
    private readonly Lock _sync = new();

    /// <summary>Outstanding join count per channel key. Keys compare with the default string
    /// comparer, which is ordinal, matching the hub's group-name semantics.</summary>
    private readonly Dictionary<string, int> _counts = [];

    /// <summary>Records a join for <paramref name="channelKey"/>.</summary>
    /// <param name="channelKey">The channel key.</param>
    /// <returns>
    /// <see langword="true"/> on the first outstanding join (0 to 1), when the server must be told
    /// to join; otherwise <see langword="false"/>.
    /// </returns>
    internal bool AddRef(string channelKey)
    {
        lock (_sync)
        {
            _counts.TryGetValue(channelKey, out int current);
            _counts[channelKey] = current + 1;
            return current == 0;
        }
    }

    /// <summary>
    /// Records a leave for <paramref name="channelKey"/>. A leave with no matching join is a no-op:
    /// the count never goes negative.
    /// </summary>
    /// <param name="channelKey">The channel key.</param>
    /// <returns>
    /// <see langword="true"/> on the last outstanding leave (1 to 0), when the server must be told
    /// to leave; otherwise <see langword="false"/>.
    /// </returns>
    internal bool Release(string channelKey)
    {
        lock (_sync)
        {
            if (!_counts.TryGetValue(channelKey, out int current))
            {
                return false;
            }

            int next = current - 1;
            if (next <= 0)
            {
                _counts.Remove(channelKey);
                return true;
            }

            _counts[channelKey] = next;
            return false;
        }
    }

    /// <summary>
    /// Gets the channel keys with at least one outstanding join, for replay after a reconnect.
    /// </summary>
    /// <returns>The distinct channel keys still held.</returns>
    internal string[] Snapshot()
    {
        lock (_sync)
        {
            return [.. _counts.Keys];
        }
    }

    /// <summary>Gets the number of outstanding joins for <paramref name="channelKey"/>.</summary>
    /// <param name="channelKey">The channel key.</param>
    /// <returns>The outstanding join count, or zero when the channel is not held.</returns>
    internal int RefCountFor(string channelKey)
    {
        lock (_sync)
        {
            return _counts.TryGetValue(channelKey, out int count) ? count : 0;
        }
    }
}
