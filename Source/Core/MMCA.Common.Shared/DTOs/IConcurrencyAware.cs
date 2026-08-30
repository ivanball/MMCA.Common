namespace MMCA.Common.Shared.DTOs;

/// <summary>
/// Contract for read DTOs that expose the current optimistic-concurrency token. The API renders it
/// as the response <c>ETag</c>, and a client echoes it back in the <c>If-Match</c> header of its next
/// write, where <c>[SupportsIfMatch]</c> turns it into the original <c>RowVersion</c> the persistence
/// layer compares against (see <c>IWriteRepository.SetOriginalRowVersion</c>).
/// </summary>
/// <remarks>
/// The token is the header's whole content, so it is never optional: a DTO read from a persisted
/// aggregate always has one (<c>AuditableBaseEntity.RowVersion</c> is non-null), and a write that
/// states no precondition is refused with <c>428 Precondition Required</c> rather than falling back
/// to last-write-wins. Update requests carry no token: the precondition travels in the header alone.
/// </remarks>
public interface IConcurrencyAware
{
    /// <summary>The optimistic-concurrency token (SQL Server <c>rowversion</c>) of this version of the resource.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "byte[] is required to round-trip the EF rowversion concurrency token")]
    byte[] RowVersion { get; init; }
}
