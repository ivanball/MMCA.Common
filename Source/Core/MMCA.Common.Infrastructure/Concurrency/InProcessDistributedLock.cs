using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Infrastructure.Concurrency;

/// <summary>
/// In-process fallback <see cref="IDistributedLock"/> for hosts with no Redis connection
/// registered. Serializes callers inside this process only.
/// <para>
/// This is the degraded mode, and it is logged once so it is visible rather than silent: with more
/// than one replica each gets its own instance of this lock, so the guarded section still runs once
/// per replica. It is correct for a single-replica deployment, for local development, and for tests.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// Keyed on the exact key rather than on hashed stripes
/// (<see cref="MMCA.Common.Shared.Concurrency.KeyedSemaphoreStripe"/>): stripes let two unrelated
/// keys share a semaphore, which is harmless for a caller that waits indefinitely but not for this
/// contract, where a bounded wait turns that false sharing into a spurious "held elsewhere" answer
/// for a key nobody holds. The held-key table is bounded by the number of locks held right now, not
/// by how many distinct keys the process has ever seen, because entries are removed on release.
/// </para>
/// <para>
/// <c>ttl</c> is accepted and ignored. It exists to bound a holder that died without releasing;
/// here the holder is a task in this process, and if the process dies the table dies with it.
/// </para>
/// </remarks>
internal sealed partial class InProcessDistributedLock(ILogger<InProcessDistributedLock> logger) : IDistributedLock
{
    /// <summary>Gap between acquisition attempts while waiting for a holder to release.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly ConcurrentDictionary<string, byte> _held = new(StringComparer.Ordinal);

    /// <summary>Set once (via <see cref="Interlocked"/>) after the degradation is logged, so a steady state warns once rather than on every request.</summary>
    private int _degradationWarned;

    /// <inheritdoc />
    public async Task<IAsyncDisposable?> TryAcquireAsync(
        string key,
        TimeSpan ttl,
        TimeSpan wait,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero);

        if (Interlocked.Exchange(ref _degradationWarned, 1) == 0)
        {
            LogProcessLocalLocking(logger);
        }

        var startedAt = Stopwatch.GetTimestamp();

        while (true)
        {
            if (_held.TryAdd(key, 0))
            {
                return new InProcessLockHandle(_held, key);
            }

            if (Stopwatch.GetElapsedTime(startedAt) >= wait)
            {
                return null;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Distributed locking is process-local: no IConnectionMultiplexer is registered, so a critical section guarded by IDistributedLock still runs once per replica. Register a Redis client (AddRedisClient) to make it exclusive across replicas.")]
    private static partial void LogProcessLocalLocking(ILogger logger);

    /// <summary>Releases exactly the acquisition it was created for, once.</summary>
    private sealed class InProcessLockHandle(ConcurrentDictionary<string, byte> held, string key) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                held.TryRemove(key, out _);
            }

            return ValueTask.CompletedTask;
        }
    }
}
