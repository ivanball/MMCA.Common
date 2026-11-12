using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.ProcessManagers;

/// <summary>
/// Arms a process timeout: schedules its internal command to run once the window has elapsed.
/// Registered by <c>AddProcessManagers()</c>.
/// </summary>
public interface IProcessTimeoutScheduler
{
    /// <summary>
    /// Schedules <paramref name="timeout"/> to run <paramref name="after"/> from now. The write inherits
    /// the internal command scheduler's atomicity: enrolled in the active transaction when there is one,
    /// saved outright otherwise.
    /// </summary>
    /// <typeparam name="TProcessId">The process aggregate's identifier type.</typeparam>
    /// <param name="timeout">The timeout command to schedule.</param>
    /// <param name="after">How long from now the timeout becomes due; strictly positive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The scheduled row's id; a <c>ProcessManagers.TimeoutNotInFuture</c> validation failure when
    /// <paramref name="after"/> is zero or negative; or the internal command scheduler's own failure.
    /// </returns>
    Task<Result<Guid>> ArmAsync<TProcessId>(
        IProcessTimeoutCommand<TProcessId> timeout,
        TimeSpan after,
        CancellationToken cancellationToken = default)
        where TProcessId : notnull;
}
