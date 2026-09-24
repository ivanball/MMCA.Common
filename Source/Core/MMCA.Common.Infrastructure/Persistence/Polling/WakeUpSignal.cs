namespace MMCA.Common.Infrastructure.Persistence.Polling;

/// <summary>
/// The <see cref="SemaphoreSlim"/>-based wake-up signal behind both <c>OutboxSignal</c> and
/// <c>InternalCommandSignal</c>.
/// <para>
/// The semaphore is capped at one permit on purpose. Each processor drains a whole batch per cycle,
/// so one pending wake-up is all the information a burst of writes carries. An uncapped semaphore
/// (the default <c>new SemaphoreSlim(0)</c> maxCount of <see cref="int.MaxValue"/>) accumulated one
/// permit per <see cref="Signal"/> call, so N writes in a burst made <see cref="WaitAsync"/> return
/// immediately N times and each of those cycles issued a candidate-fetch query per relational data
/// source that returned nothing. Surplus signals were harmless for correctness but not for cost.
/// With the cap, the surplus is absorbed here.
/// </para>
/// </summary>
internal sealed class WakeUpSignal : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    /// <summary>Requests a wake-up; a no-op when one is already pending.</summary>
    public void Signal()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending and one batch drains everything: nothing to add.
        }
    }

    /// <summary>Waits for a wake-up or the timeout, whichever comes first.</summary>
    /// <param name="timeout">The longest time to wait.</param>
    /// <param name="cancellationToken">Cancels the wait; the cancellation propagates.</param>
    /// <returns>A task that completes on a wake-up or the timeout.</returns>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _semaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Propagate shutdown.
        }
    }

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();
}
