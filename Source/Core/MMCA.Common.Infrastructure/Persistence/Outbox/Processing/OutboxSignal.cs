namespace MMCA.Common.Infrastructure.Persistence.Outbox.Processing;

/// <summary>
/// <see cref="SemaphoreSlim"/>-based implementation of <see cref="IOutboxSignal"/>.
/// <para>
/// The semaphore is capped at one permit on purpose. <see cref="OutboxProcessor"/> drains every
/// pending message in a single batch, so one pending wake-up is all the information a burst of
/// saves carries. An uncapped semaphore (the default <c>new SemaphoreSlim(0)</c> maxCount of
/// <see cref="int.MaxValue"/>) accumulated one permit per <see cref="Signal"/> call, so N saves in
/// a burst made <see cref="WaitAsync"/> return immediately N times and each of those cycles issued
/// a candidate-fetch query per relational data source that returned nothing. Surplus signals were
/// harmless for correctness but not for cost. With the cap, the surplus is absorbed here.
/// </para>
/// </summary>
public sealed class OutboxSignal : IOutboxSignal, IDisposable
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
