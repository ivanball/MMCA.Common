namespace MMCA.Common.Shared.DTOs;

/// <summary>
/// Reusable request body for lifecycle/state-transition endpoints (publish, cancel, open,
/// approve, ...) whose only payload is the optimistic-concurrency token (ADR-035). The client
/// echoes the <see cref="RowVersion"/> from the DTO it acted on, so a transition decided
/// against a stale view of the aggregate surfaces as 409 Conflict instead of applying
/// silently. Bind it as an OPTIONAL body (ASP.NET Core <c>EmptyBodyBehavior.Allow</c>) so
/// body-less legacy callers keep working and simply skip the stale-view check; a null
/// <see cref="RowVersion"/> also skips it (see <c>IWriteRepository.SetOriginalRowVersion</c>).
/// </summary>
public sealed record class ConcurrencyTokenRequest : IConcurrencyAware
{
    /// <inheritdoc />
    public byte[]? RowVersion { get; init; }
}
