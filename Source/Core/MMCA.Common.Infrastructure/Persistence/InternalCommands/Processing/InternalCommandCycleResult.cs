namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

/// <summary>
/// Outcome of one internal-command polling cycle, used by <see cref="InternalCommandProcessor"/> to
/// decide how long to wait before the next one.
/// </summary>
/// <param name="HasMoreDueWork">
/// Whether a full batch of due commands was claimed and at least one made progress (completed or
/// dead-lettered), so more due rows may be waiting and the processor re-polls immediately. The
/// progress requirement prevents hot-spinning when an entire batch fails and stays due.
/// </param>
/// <param name="EarliestUpcoming">
/// The <see cref="InternalCommandMessage.ScheduledOn"/> of the oldest row that is not yet due, or
/// <see langword="null"/> when none is waiting. The processor smart-waits until that instant instead
/// of sleeping the full polling interval.
/// </param>
internal readonly record struct InternalCommandCycleResult(bool HasMoreDueWork, DateTime? EarliestUpcoming);
