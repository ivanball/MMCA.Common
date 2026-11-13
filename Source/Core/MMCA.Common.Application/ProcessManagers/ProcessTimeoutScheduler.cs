using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.ProcessManagers;

/// <summary>
/// Default <see cref="IProcessTimeoutScheduler"/>: one <see cref="IInternalCommandScheduler"/> call
/// through its <c>runAt</c> overload, at the injected clock's now plus the delay, so the due instant
/// comes from the same clock the timeout handler later decides with.
/// </summary>
/// <param name="internalCommandScheduler">Writes the internal command row.</param>
/// <param name="timeProvider">The clock the due instant is computed from.</param>
public sealed class ProcessTimeoutScheduler(
    IInternalCommandScheduler internalCommandScheduler,
    TimeProvider timeProvider) : IProcessTimeoutScheduler
{
    /// <summary>The error code returned when the delay is zero or negative.</summary>
    internal const string TimeoutNotInFutureCode = "ProcessManagers.TimeoutNotInFuture";

    /// <inheritdoc />
    public Task<Result<Guid>> ArmAsync<TProcessId>(
        IProcessTimeoutCommand<TProcessId> timeout,
        TimeSpan after,
        CancellationToken cancellationToken = default)
        where TProcessId : notnull
    {
        ArgumentNullException.ThrowIfNull(timeout);

        if (after <= TimeSpan.Zero)
        {
            return Task.FromResult(Result.Failure<Guid>(Error.Validation(
                TimeoutNotInFutureCode,
                "A process timeout must be armed for a strictly positive delay.",
                nameof(ArmAsync),
                nameof(after))));
        }

        return internalCommandScheduler.ScheduleAsync(timeout, timeProvider.GetUtcNow() + after, cancellationToken);
    }
}
