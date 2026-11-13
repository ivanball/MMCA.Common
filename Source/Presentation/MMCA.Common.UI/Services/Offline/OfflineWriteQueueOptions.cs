namespace MMCA.Common.UI.Services.Offline;

/// <summary>Options of the <see cref="OfflineWriteQueue"/>, configured through <c>AddOfflineWriteQueue</c>.</summary>
public sealed class OfflineWriteQueueOptions
{
    /// <summary>Gets or sets the local store key the whole queue is kept under. Defaults to <c>mmca.offline-writes</c>.</summary>
    public string StorageKey { get; set; } = "mmca.offline-writes";

    /// <summary>
    /// Gets or sets how long a queued write may wait. An older write is discarded and reported as
    /// <see cref="OfflineWriteOutcome.Expired"/> instead of replayed. Defaults to 24 hours, the window in
    /// which the server replays an idempotency key, so a write older than that could otherwise run twice.
    /// </summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Gets or sets how many writes the queue holds before an enqueue is refused. Defaults to 200.</summary>
    public int MaxPendingWrites { get; set; } = 200;
}
