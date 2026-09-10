namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

/// <summary>
/// <see cref="SemaphoreSlim"/>-based implementation of <see cref="IInternalCommandSignal"/>.
/// <para>
/// The semaphore is capped at one permit for the same reason the outbox signal is: the processor
/// drains a whole batch per cycle, so one pending wake-up is all the information a burst of
/// schedules carries. An uncapped semaphore would return immediately once per surplus signal and
/// each of those cycles would issue a candidate query per relational source that returned nothing.
/// </para>
/// </summary>
public sealed class InternalCommandSignal : IInternalCommandSignal, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    /// <inheritdoc />
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

    /// <inheritdoc />
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
