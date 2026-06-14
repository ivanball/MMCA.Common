namespace MMCA.Common.Shared.DTOs;

/// <summary>
/// Contract for DTOs and update requests that round-trip an optimistic-concurrency token.
/// Read DTOs expose the current <see cref="RowVersion"/> so a client can echo it back on the
/// next update; update requests carry the client's last-seen value so the persistence layer can
/// detect a conflicting concurrent modification (see <c>IWriteRepository.SetOriginalRowVersion</c>).
/// </summary>
/// <remarks>
/// Without this round-trip an update loads the row fresh and saves it, so two concurrent editors
/// silently overwrite each other (last-write-wins) and the mapped <c>409 Conflict</c> never fires.
/// </remarks>
public interface IConcurrencyAware
{
    /// <summary>
    /// The optimistic-concurrency token (SQL Server <c>rowversion</c>) the client last observed.
    /// Null or empty on creation or from legacy clients, in which case the conflict check is skipped.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "byte[] is required to round-trip the EF rowversion concurrency token")]
    byte[]? RowVersion { get; init; }
}
