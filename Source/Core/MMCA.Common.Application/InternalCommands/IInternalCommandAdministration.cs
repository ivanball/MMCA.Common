using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.InternalCommands;

/// <summary>
/// Operator surface over the <c>InternalCommands</c> tables this host owns: read the backlog,
/// inspect the dead letters, put one back in the queue, and purge what has already run. It is the
/// job-queue counterpart of <c>IOutboxAdministration</c>, and exists for the same reason: without it
/// a command that exhausted its attempts has no way back into execution except hand-edited
/// production SQL.
/// <para>
/// Expose it from an admin endpoint, a support command, or a scheduled job. Every method returns
/// <see cref="Result{T}"/>: an unknown source name or an unreachable database is an expected failure
/// an operator screen renders, not an exception.
/// </para>
/// </summary>
public interface IInternalCommandAdministration
{
    /// <summary>
    /// Counts rows still awaiting execution (not processed, not dead-lettered, attempts not
    /// exhausted) across every internal-command source this host owns or just the one named.
    /// Rows whose scheduled instant is still in the future are included: they are backlog the host
    /// has accepted, not work it has finished.
    /// </summary>
    /// <param name="dataSource">Source name to restrict to; <see langword="null"/> counts every source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The pending row count.</returns>
    Task<Result<long>> CountPendingAsync(string? dataSource, CancellationToken cancellationToken);

    /// <summary>
    /// Lists dead-lettered rows (attempts exhausted, never completed) oldest first, across every
    /// internal-command source this host owns or just the one named.
    /// </summary>
    /// <param name="dataSource">
    /// Source name to restrict to, as reported by <see cref="InternalCommandDeadLetter.DataSource"/>;
    /// <see langword="null"/> lists every source.
    /// </param>
    /// <param name="skip">Rows to skip, for paging.</param>
    /// <param name="take">Maximum rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page of dead letters, oldest first.</returns>
    Task<Result<IReadOnlyList<InternalCommandDeadLetter>>> ListDeadLettersAsync(
        string? dataSource,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns dead-lettered rows to the queue: attempts back to zero, the dead-letter stamp and the
    /// claim lease cleared, so the next poll cycle picks them up. <c>LastError</c> is deliberately
    /// KEPT, because the reason a command failed is the first thing anyone asks after a requeue, and
    /// the scheduled instant is untouched, so a requeued row is immediately due.
    /// </summary>
    /// <param name="dataSource">
    /// Source name to restrict to; <see langword="null"/> requeues across every source this host owns.
    /// </param>
    /// <param name="ids">
    /// The specific rows to requeue, or <see langword="null"/>/empty to requeue EVERY dead letter in
    /// the selected scope.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows returned to the queue.</returns>
    Task<Result<int>> RequeueAsync(
        string? dataSource,
        IReadOnlyCollection<Guid>? ids,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes completed rows older than <paramref name="olderThan"/>, on demand rather than waiting
    /// for the retention sweep. Only rows carrying a completion stamp are eligible; pending and
    /// dead-lettered rows are never touched by this call.
    /// </summary>
    /// <param name="dataSource">Source name to restrict to; <see langword="null"/> purges every source.</param>
    /// <param name="olderThan">
    /// Age threshold measured against the completion stamp. <see cref="TimeSpan.Zero"/> purges every
    /// completed row.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows deleted.</returns>
    Task<Result<int>> PurgeProcessedAsync(
        string? dataSource,
        TimeSpan olderThan,
        CancellationToken cancellationToken);
}

/// <summary>
/// One dead-lettered internal-command row, flattened for an operator view. The command PAYLOAD is
/// deliberately not projected: it can carry personal data (ADR-005), and nothing an operator decides
/// about a requeue depends on reading it.
/// </summary>
/// <param name="Id">The row id, the handle <see cref="IInternalCommandAdministration.RequeueAsync"/> takes.</param>
/// <param name="DataSource">The source whose table holds the row.</param>
/// <param name="CommandType">The stored command type name.</param>
/// <param name="ScheduledOn">The instant the command was scheduled to run at.</param>
/// <param name="CreatedOn">The instant the row was written.</param>
/// <param name="Attempts">Attempts made before the row was abandoned.</param>
/// <param name="LastError">The failure recorded on the final attempt.</param>
/// <param name="DeadLetteredOn">The instant the row was abandoned.</param>
public sealed record InternalCommandDeadLetter(
    Guid Id,
    string DataSource,
    string CommandType,
    DateTime ScheduledOn,
    DateTime CreatedOn,
    int Attempts,
    string? LastError,
    DateTime? DeadLetteredOn);
