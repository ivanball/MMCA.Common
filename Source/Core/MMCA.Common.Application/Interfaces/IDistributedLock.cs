namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// Mutual exclusion on a logical key across every replica of a service, for critical sections a
/// per-process lock cannot protect.
/// <para>
/// A <see cref="System.Threading.SemaphoreSlim"/> (or
/// <see cref="MMCA.Common.Shared.Concurrency.KeyedSemaphoreStripe"/>) only serializes callers inside
/// one process. A service scaled to more than one replica runs the same critical section once per
/// replica, so anything that relies on "only one of these runs at a time" (the idempotency filter's
/// execute-then-store window, a single-writer import, a leader-elected job) needs the lock to live
/// where all the replicas can see it.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// Implementations are singletons and must be safe to call concurrently.
/// </para>
/// <para>
/// The lock is NOT reentrant: a caller that already holds <c>key</c> and asks for it again waits
/// for itself and then fails to acquire.
/// </para>
/// <para>
/// This is a best-effort lock, not a consensus protocol. A holder that is paused past its
/// time-to-live loses the lock without knowing it, so the guarded section must stay correct (if
/// slower or duplicated) when exclusion is lost. Use it to collapse duplicate work, never as the
/// only guard on a correctness invariant that persistence can enforce.
/// </para>
/// </remarks>
public interface IDistributedLock
{
    /// <summary>
    /// Attempts to take the lock on <paramref name="key"/>, waiting up to <paramref name="wait"/>
    /// for a current holder to release it.
    /// </summary>
    /// <param name="key">The logical key to lock. Callers sharing one backing store must agree on it.</param>
    /// <param name="ttl">
    /// How long the lock survives without an explicit release. This is the crash guard: a holder
    /// that dies mid-section cannot wedge the key, because the entry expires on its own. Set it
    /// comfortably above the guarded section's expected duration, since work that outlives the TTL
    /// is no longer protected.
    /// </param>
    /// <param name="wait">
    /// How long to wait for the lock before giving up. <see cref="TimeSpan.Zero"/> makes the call a
    /// single non-blocking attempt.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait, not the work that follows it.</param>
    /// <returns>
    /// A handle whose asynchronous disposal releases the lock, or <see langword="null"/> when the
    /// lock was still held elsewhere after <paramref name="wait"/> elapsed. Dispose the handle
    /// inside an <see langword="await"/> <see langword="using"/> so the release happens even when
    /// the guarded work throws.
    /// </returns>
    /// <remarks>
    /// Release is owner-scoped: a handle releases only the acquisition it represents. Disposing a
    /// handle whose TTL already expired (so another caller now holds the key) is a no-op rather
    /// than a release of the new holder's lock. Disposal is idempotent.
    /// </remarks>
    Task<IAsyncDisposable?> TryAcquireAsync(
        string key,
        TimeSpan ttl,
        TimeSpan wait,
        CancellationToken cancellationToken = default);
}
