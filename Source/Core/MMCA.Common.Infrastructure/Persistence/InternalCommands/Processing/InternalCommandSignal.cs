using MMCA.Common.Infrastructure.Persistence.Polling;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

/// <summary>
/// <see cref="SemaphoreSlim"/>-based implementation of <see cref="IInternalCommandSignal"/>, the wake-up
/// <see cref="InternalCommandProcessor"/> waits on between cycles. The semaphore, capped at one permit so a burst
/// of signals costs one extra cycle rather than one per signal, is the shared <see cref="WakeUpSignal"/>.
/// <para>
/// A dedicated delivery lane (<see cref="InternalCommandLaneProcessor"/>) waits on a second wake of its
/// own, created when the lane is constructed, so one <see cref="Signal"/> wakes the shared loop and the
/// lane without either stealing the other's permit, and a signal sent before the lane's first wait is kept
/// for it. Without a lane that wake never exists.
/// </para>
/// </summary>
public sealed class InternalCommandSignal : IInternalCommandSignal, IDisposable
{
    private readonly WakeUpSignal _signal = new();
    private WakeUpSignal? _laneWake;

    /// <summary>Gets a value indicating whether a lane has created the lane wake (test hook).</summary>
    internal bool HasLaneWake => Volatile.Read(ref _laneWake) is not null;

    /// <inheritdoc />
    public void Signal()
    {
        _signal.Signal();
        Volatile.Read(ref _laneWake)?.Signal();
    }

    /// <inheritdoc />
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);

    /// <summary>
    /// Creates the lane's own wake if it does not exist yet. A lane calls this when it is constructed, so
    /// a <see cref="Signal"/> sent between its start and its first wait is not lost.
    /// </summary>
    internal void EnsureLaneWake() => _ = GetOrCreateLaneWake();

    /// <summary>
    /// Waits on the lane's own wake for a signal or the timeout, whichever comes first; the wake is
    /// created if no lane has created it yet.
    /// </summary>
    /// <param name="timeout">The longest time to wait.</param>
    /// <param name="cancellationToken">Cancels the wait; the cancellation propagates.</param>
    /// <returns>A task that completes on a wake-up or the timeout.</returns>
    internal Task WaitForLaneAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        GetOrCreateLaneWake().WaitAsync(timeout, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        _signal.Dispose();
        Volatile.Read(ref _laneWake)?.Dispose();
    }

    private WakeUpSignal GetOrCreateLaneWake()
    {
        if (Volatile.Read(ref _laneWake) is { } existing)
        {
            return existing;
        }

        var created = new WakeUpSignal();
        var winner = Interlocked.CompareExchange(ref _laneWake, created, null);
        if (winner is null)
        {
            return created;
        }

        created.Dispose();
        return winner;
    }
}
