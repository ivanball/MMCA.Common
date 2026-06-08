namespace MMCA.Common.Infrastructure.Persistence.Outbox;

/// <summary>
/// Outcome of one outbox polling cycle, used by <see cref="OutboxProcessor"/> to decide
/// how long to wait before the next cycle.
/// </summary>
/// <param name="HasMoreEligibleWork">
/// Whether a full batch of eligible messages was fetched and at least one made progress
/// (dispatched or dead-lettered) — more eligible rows may be waiting, so the processor
/// re-polls immediately. The progress requirement prevents hot-spinning when an entire
/// batch fails dispatch and stays eligible.
/// </param>
/// <param name="EarliestPendingOccurredOn">
/// The <see cref="OutboxMessage.OccurredOn"/> of the oldest message that is not yet eligible
/// (younger than the processing delay), or <see langword="null"/> when none are pending.
/// The processor smart-waits until this message becomes eligible instead of sleeping the
/// full polling interval.
/// </param>
internal readonly record struct OutboxCycleResult(bool HasMoreEligibleWork, DateTime? EarliestPendingOccurredOn);
