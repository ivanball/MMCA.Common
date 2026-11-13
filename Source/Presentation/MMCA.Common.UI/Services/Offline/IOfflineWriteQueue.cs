using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.UI.Services.Offline;

/// <summary>
/// A durable queue of writes made while the device was offline, kept in the device's local store
/// and replayed through the registered <see cref="IOfflineWriteHandler"/>s when it is back online.
/// Registered as a scoped service by <c>AddOfflineWriteQueue</c>; each entry belongs to the user who
/// was signed in when it was queued.
/// </summary>
public interface IOfflineWriteQueue
{
    /// <summary>
    /// Gets a value indicating whether the host registered a device-local store. False only with the
    /// default <c>NullLocalCacheStore</c>; a host that calls <c>AddBrowserDeviceCapabilities</c> (Blazor
    /// Server and WebAssembly alike) or the MAUI capabilities has one.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Raised once per write that left the queue by a replay, after the store no longer holds it.</summary>
    event EventHandler<OfflineWriteSettledEventArgs>? WriteSettled;

    /// <summary>Raised after every persisted change of the queue (enqueue, cancel, settle, attempt count).</summary>
    event EventHandler? PendingChanged;

    /// <summary>Queues a write for the current user.</summary>
    /// <typeparam name="TPayload">The payload type; serialized with <c>JsonSerializerOptions.Web</c>.</typeparam>
    /// <param name="kind">The handler key; must not be blank.</param>
    /// <param name="payload">The payload the handler reads back with <see cref="PendingWrite.ReadPayload{TPayload}"/>.</param>
    /// <param name="options">The idempotency key, row version and coalesce key, all optional.</param>
    /// <param name="cancellationToken">Cancels the store access.</param>
    /// <returns>The queued write, or <c>OfflineWrites.Unavailable</c> / <c>OfflineWrites.QueueFull</c>.</returns>
    Task<Result<PendingWrite>> EnqueueAsync<TPayload>(string kind, TPayload payload, OfflineWriteOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Lists the current user's queued writes, oldest first.</summary>
    /// <param name="cancellationToken">Cancels the store access.</param>
    /// <returns>The current user's writes in queue order.</returns>
    Task<IReadOnlyList<PendingWrite>> GetPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes every queued write of the current user with this coalesce key (ordinal). Raises no <see cref="WriteSettled"/>.</summary>
    /// <param name="coalesceKey">The coalesce key.</param>
    /// <param name="cancellationToken">Cancels the store access.</param>
    /// <returns><see langword="true"/> when at least one write was removed.</returns>
    Task<bool> CancelPendingAsync(string coalesceKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replays the current user's writes oldest first. Does nothing (returns 0) when the store is not
    /// available, the device is offline, or another replay is running.
    /// </summary>
    /// <param name="cancellationToken">Cancels the replay; the store keeps what settled before.</param>
    /// <returns>The number of writes that settled.</returns>
    Task<int> ReplayAsync(CancellationToken cancellationToken = default);
}
