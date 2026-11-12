using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.UI.Services.Offline;

/// <summary>Data of <see cref="IOfflineWriteQueue.WriteSettled"/>: one queued write left the queue.</summary>
/// <param name="write">The write that settled.</param>
/// <param name="outcome">How it settled.</param>
/// <param name="result">The handler's result, or <see langword="null"/> for <see cref="OfflineWriteOutcome.Expired"/>.</param>
public sealed class OfflineWriteSettledEventArgs(PendingWrite write, OfflineWriteOutcome outcome, Result? result) : EventArgs
{
    /// <summary>Gets the write that settled.</summary>
    public PendingWrite Write { get; } = write;

    /// <summary>Gets how the write settled.</summary>
    public OfflineWriteOutcome Outcome { get; } = outcome;

    /// <summary>Gets the handler's result, or <see langword="null"/> for <see cref="OfflineWriteOutcome.Expired"/>.</summary>
    public Result? Result { get; } = result;
}
