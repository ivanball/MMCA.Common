namespace MMCA.Common.UI.Services.Offline;

/// <summary>Per-write options for <see cref="IOfflineWriteQueue.EnqueueAsync{TPayload}"/>.</summary>
public sealed record OfflineWriteOptions
{
    /// <summary>Gets the idempotency key the replay sends; <see langword="null"/> generates a new GUID in "N" form.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Gets the row version the write is based on; the replay sends it as <c>If-Match</c> (<see cref="PendingWrite.IfMatch"/>).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "The EF rowversion token in its wire shape (byte[]), the same exemption the repo grants DTO, Request and Command files; it is copied onto the queued PendingWrite.")]
    public byte[]? BasedOnRowVersion { get; init; }

    /// <summary>Gets the key that pairs writes which cancel each other out (see <see cref="IOfflineWriteQueue.CancelPendingAsync"/>).</summary>
    public string? CoalesceKey { get; init; }
}
