using MMCA.Common.Domain.Auth;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// One sign-in as the store aggregates it (<see cref="IRefreshSessionStore.GetRecentSignInsAsync"/>):
/// its identifier, the instant its earliest retained session was issued, and its newest session (the
/// head), which carries the last activity, the device and how the sign-in ended.
/// </summary>
/// <param name="SignInId">
/// The sign-in's identifier (<see cref="RefreshSession.SignInId"/>), or, for a row written before that
/// column existed, the row's own <see cref="RefreshSession.Id"/>.
/// </param>
/// <param name="StartedAt">The UTC issue instant of the sign-in's earliest retained session.</param>
/// <param name="Head">
/// The sign-in's newest session. Read-only: a store may return it untracked, so a mutation on it is
/// never persisted.
/// </param>
public sealed record RefreshSessionSignIn(Guid SignInId, DateTime StartedAt, RefreshSession Head);
