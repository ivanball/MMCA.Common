using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.ProcessManagers;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.ProcessManagers;

/// <summary>
/// Handles a fired process timeout: loads the process aggregate tracked, lets it decide the timeout
/// against the injected clock, and saves once when it applied the timeout.
/// </summary>
/// <remarks>
/// <para>
/// Every outcome completes the internal command row with <see cref="Result.Success()"/> except a
/// save failure that is not a concurrency conflict, which propagates so the row retries with backoff.
/// A missing process, a timeout that does not apply, an early (stale or re-armed) row and a refused
/// transition are all decided by stored state, so retrying them cannot change the outcome. A lost
/// concurrency race means another writer already moved the process.
/// </para>
/// <para>
/// A <c>NotYetDue</c> decision completes the row without scheduling another, so every step that moves
/// the process and still wants the timeout schedules it again, and a host keeps a reconciliation sweep
/// for the cases no step covers.
/// </para>
/// <para>
/// A consumer derives one sealed class per timeout command so the module scan registers it as a
/// closed <c>ICommandHandler&lt;TCommand, Result&gt;</c> the decorator pipeline wraps.
/// </para>
/// </remarks>
/// <typeparam name="TCommand">The consumer-owned timeout command.</typeparam>
/// <typeparam name="TProcess">The process aggregate.</typeparam>
/// <typeparam name="TProcessId">The process aggregate's identifier type.</typeparam>
/// <param name="unitOfWork">Loads and saves the process.</param>
/// <param name="concurrencyConflictDetector">Classifies a save failure as a lost race.</param>
/// <param name="timeProvider">The clock the aggregate decides against.</param>
public abstract class ProcessTimeoutHandlerBase<TCommand, TProcess, TProcessId>(
    IUnitOfWork unitOfWork,
    IConcurrencyConflictDetector concurrencyConflictDetector,
    TimeProvider timeProvider)
    : ICommandHandler<TCommand, Result>
    where TCommand : IProcessTimeoutCommand<TProcessId>
    where TProcess : ProcessManagerAggregateRoot<TProcessId>
    where TProcessId : notnull
{
    /// <summary>Gets the navigation paths loaded with the process. Defaults to none.</summary>
    protected virtual IEnumerable<string> Includes => [];

    /// <inheritdoc />
    public async Task<Result> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var timeout = ResolveTimeout(command);
        var processId = command.ProcessId;

        var process = await unitOfWork.GetRepository<TProcess, TProcessId>()
            .GetByIdAsync(processId, Includes, asTracking: true, cancellationToken)
            .ConfigureAwait(false);

        if (process is null)
        {
            OnHandled(command, Report(ProcessTimeoutHandling.ProcessMissing, processId, timeout, stateBefore: null));
            return Result.Success();
        }

        var stateBefore = DescribeState(process);
        var decision = process.DecideTimeout(timeout, timeProvider.GetUtcNow().UtcDateTime);

        var handling = decision.Outcome switch
        {
            ProcessTimeoutOutcome.Applied => ProcessTimeoutHandling.Applied,
            ProcessTimeoutOutcome.NotYetDue => ProcessTimeoutHandling.NotYetDue,
            ProcessTimeoutOutcome.Rejected => ProcessTimeoutHandling.Rejected,
            ProcessTimeoutOutcome.NotApplicable => ProcessTimeoutHandling.NotApplicable,
            _ => ProcessTimeoutHandling.NotApplicable,
        };

        if (handling != ProcessTimeoutHandling.Applied)
        {
            OnHandled(
                command,
                new ProcessTimeoutReport<TProcessId>(handling, processId, timeout.Name, decision.DueAt, decision.Errors, stateBefore));
            return Result.Success();
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (concurrencyConflictDetector.IsConcurrencyConflict(exception))
        {
            OnHandled(command, Report(ProcessTimeoutHandling.LostRace, processId, timeout, stateBefore));
            return Result.Success();
        }

        OnHandled(command, Report(ProcessTimeoutHandling.Applied, processId, timeout, stateBefore));
        return Result.Success();
    }

    /// <summary>Resolves the timeout <paramref name="command"/> fires, typically from the consumer's settings.</summary>
    /// <param name="command">The fired command.</param>
    /// <returns>The named timeout window. Called once per command, before the process is loaded.</returns>
    protected abstract ProcessTimeout ResolveTimeout(TCommand command);

    /// <summary>
    /// Describes the process state for the report, captured BEFORE the aggregate decides. Defaults to
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="process">The loaded process.</param>
    /// <returns>A short description of the state, or <see langword="null"/>.</returns>
    protected virtual string? DescribeState(TProcess process) => null;

    /// <summary>Receives the report of every handled command, for logging. Does nothing by default.</summary>
    /// <param name="command">The handled command.</param>
    /// <param name="report">What was done.</param>
    protected virtual void OnHandled(TCommand command, ProcessTimeoutReport<TProcessId> report)
    {
    }

    private static ProcessTimeoutReport<TProcessId> Report(
        ProcessTimeoutHandling handling,
        TProcessId processId,
        ProcessTimeout timeout,
        string? stateBefore) =>
        new(handling, processId, timeout.Name, DueAt: null, Errors: [], stateBefore);
}
