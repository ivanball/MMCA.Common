using MMCA.Common.Infrastructure.Persistence.Polling;

namespace MMCA.Common.Infrastructure.Persistence.Outbox.Processing;

/// <summary>
/// <see cref="SemaphoreSlim"/>-based implementation of <see cref="IOutboxSignal"/>, the wake-up
/// <see cref="OutboxProcessor"/> waits on between cycles. The semaphore, capped at one permit so a burst
/// of signals costs one extra cycle rather than one per signal, is the shared <see cref="WakeUpSignal"/>.
/// </summary>
public sealed class OutboxSignal : IOutboxSignal, IDisposable
{
    private readonly WakeUpSignal _signal = new();

    /// <inheritdoc />
    public void Signal() => _signal.Signal();

    /// <inheritdoc />
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _signal.Dispose();
}
