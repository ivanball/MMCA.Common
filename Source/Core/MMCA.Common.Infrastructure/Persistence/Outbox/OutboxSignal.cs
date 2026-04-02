namespace MMCA.Common.Infrastructure.Persistence.Outbox;

/// <summary>
/// <see cref="SemaphoreSlim"/>-based implementation of <see cref="IOutboxSignal"/>.
/// Multiple rapid <see cref="Signal"/> calls are safe — the semaphore count increments,
/// but the <see cref="OutboxProcessor"/> processes all pending messages in a single batch,
/// so extra signals are harmless.
/// </summary>
public sealed class OutboxSignal : IOutboxSignal, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(0);

    /// <inheritdoc />
    public void Signal()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signaled — safe to ignore.
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
