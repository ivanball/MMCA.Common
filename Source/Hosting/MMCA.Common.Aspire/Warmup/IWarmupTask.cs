namespace MMCA.Common.Aspire.Warmup;

/// <summary>
/// A unit of work executed once at host startup to pre-populate caches, open connection pools,
/// and otherwise eliminate lazy initialisation from the first user request. Tasks run in parallel
/// after the host has started; failures are logged but do not prevent the readiness gate from
/// opening so a transient dependency outage cannot wedge the replica permanently out of rotation.
/// </summary>
public interface IWarmupTask
{
    /// <summary>Stable name used in logs and metrics.</summary>
    string Name { get; }

    /// <summary>Performs the warm-up work.</summary>
    Task ExecuteAsync(CancellationToken cancellationToken);
}
