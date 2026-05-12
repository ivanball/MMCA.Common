namespace MMCA.Common.Aspire.Warmup;

/// <summary>
/// Thread-safe flag flipped to <see langword="true"/> exactly once when the
/// <see cref="WarmupHostedService"/> finishes running all registered <see cref="IWarmupTask"/>s.
/// Consumed by <see cref="WarmupReadinessHealthCheck"/> so the <c>/health/ready</c> endpoint
/// reports unhealthy until the gate opens; ACA readiness probes therefore keep traffic away
/// from a replica that is still warming.
/// </summary>
public sealed class WarmupReadinessGate
{
    private int _isReady;

    /// <summary>Whether warm-up has completed and the replica is ready to serve traffic.</summary>
    public bool IsReady => Volatile.Read(ref _isReady) == 1;

    /// <summary>Marks warm-up complete. Idempotent.</summary>
    internal void MarkReady() => Interlocked.Exchange(ref _isReady, 1);
}
