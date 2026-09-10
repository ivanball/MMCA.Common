using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.InternalCommands;

/// <summary>
/// Schedules an <see cref="IInternalCommand"/> for durable, deferred execution: the command is
/// written to the host's <c>InternalCommands</c> table and executed later by the framework's
/// processor through the ordinary CQRS pipeline.
/// <para>
/// The write rides the caller's own unit of work. Scheduling from inside an <c>ITransactional</c>
/// command therefore commits with the aggregate change or rolls back with it: a transaction that
/// aborts schedules nothing. This is the same atomicity guarantee the outbox gives an event, applied
/// to work the host wants to do rather than to a message it wants to publish.
/// </para>
/// </summary>
public interface IInternalCommandScheduler
{
    /// <summary>
    /// Schedules <paramref name="command"/> to run at <paramref name="runAt"/>, or as soon as the
    /// processor next polls when that is <see langword="null"/> or already in the past.
    /// </summary>
    /// <param name="command">The command to run later. Its payload must round-trip through JSON.</param>
    /// <param name="runAt">
    /// The earliest instant the command may run, converted to UTC. <see langword="null"/> means
    /// "as soon as possible". The processor never runs a row before this instant, and makes no
    /// promise about how long after it a busy queue takes to get there.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The id of the scheduled row on success, which is the handle
    /// <see cref="IInternalCommandAdministration"/> takes; a failure when the command's payload
    /// cannot be serialized or the row cannot be written.
    /// </returns>
    /// <remarks>
    /// <b>When the write is persisted.</b> With a transaction active on the target data source the
    /// row is only enrolled, and the caller's commit persists it. With no transaction active the row
    /// is saved immediately, which flushes anything else pending on that context exactly as calling
    /// the unit of work's own save would. Schedule inside an <c>ITransactional</c> command, or after
    /// your own save, when that distinction matters.
    /// </remarks>
    Task<Result<Guid>> ScheduleAsync(
        IInternalCommand command,
        DateTimeOffset? runAt = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules <paramref name="command"/> to run after <paramref name="delay"/> has elapsed.
    /// A delay of zero or less means "as soon as possible".
    /// </summary>
    /// <param name="command">The command to run later. Its payload must round-trip through JSON.</param>
    /// <param name="delay">How long from now the command becomes eligible to run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The id of the scheduled row on success; a failure when the row cannot be written.</returns>
    Task<Result<Guid>> ScheduleAsync(
        IInternalCommand command,
        TimeSpan delay,
        CancellationToken cancellationToken = default);
}
