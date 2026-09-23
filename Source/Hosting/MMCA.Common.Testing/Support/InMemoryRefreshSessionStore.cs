using MMCA.Common.Application.Auth;
using MMCA.Common.Domain.Auth;

namespace MMCA.Common.Testing.Support;

/// <summary>
/// An in-memory <see cref="IRefreshSessionStore"/> for authentication workflow tests, mirroring the EF
/// implementation's visibility rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same instances on every read.</b> The workflow revokes and rotates by mutating what the store
/// returned, so a fake that copied would silently drop every revocation.
/// </para>
/// <para>
/// <b>A staged insert is invisible until it is saved.</b> EF does not read the change tracker from a
/// database query, so <see cref="AddAsync"/> stages and <see cref="SaveChangesAsync"/> publishes; a
/// fake that made an unsaved row visible would let a missing save pass here and fail only against a
/// real database.
/// </para>
/// <para>
/// <b>Ownership is part of the lookup.</b> <see cref="FindByIdAsync"/> matches on the user as well as
/// the id, exactly like the EF store, so another account's session id is indistinguishable from one
/// that never existed.
/// </para>
/// </remarks>
public sealed class InMemoryRefreshSessionStore : IRefreshSessionStore
{
    private readonly List<RefreshSession> _staged = [];
    private readonly List<RefreshSession> _sessions = [];

    /// <summary>Gets every persisted (seeded or saved) session, in insertion order.</summary>
    public IReadOnlyList<RefreshSession> Sessions => _sessions;

    /// <summary>Gets how many times the workflow flushed the store.</summary>
    public int SaveCount { get; private set; }

    /// <summary>
    /// Gets or sets a callback that decides whether <see cref="TryRotateAsync"/> claims the rotation.
    /// A <see langword="false"/> outcome stands in for the database arbitrating a concurrent rotation
    /// of the same token: the loser writes nothing at all. Null (the default) always claims.
    /// </summary>
    public Func<bool>? RotationOutcome { get; set; }

    /// <summary>Places an already-persisted session in the store, bypassing the staged-insert path.</summary>
    /// <param name="session">The session to seed.</param>
    public void Seed(RefreshSession session) => _sessions.Add(session);

    /// <summary>Seeds a live, already-persisted session for the given user.</summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="token">The plaintext refresh token to hash into the seeded row.</param>
    /// <param name="createdAt">The UTC issue instant.</param>
    /// <param name="expiresAt">The UTC expiry instant.</param>
    /// <returns>The seeded session.</returns>
    public RefreshSession SeedLive(UserIdentifierType userId, string token, DateTime createdAt, DateTime expiresAt)
    {
        var session = RefreshSession.Create(userId, token, createdAt, expiresAt).Value!;
        _sessions.Add(session);
        return session;
    }

    /// <inheritdoc />
    public Task AddAsync(RefreshSession session, CancellationToken cancellationToken = default)
    {
        _staged.Add(session);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<RefreshSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.Find(s => string.Equals(s.TokenHash, tokenHash, StringComparison.Ordinal)));

    /// <inheritdoc />
    public Task<IReadOnlyList<RefreshSession>> GetUnrevokedByUserAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RefreshSession> live =
        [
            .. _sessions
                .Where(s => EqualityComparer<UserIdentifierType>.Default.Equals(s.UserId, userId) && !s.IsRevoked)
                .OrderBy(s => s.CreatedAt)
                .ThenBy(s => s.Id)
        ];

        return Task.FromResult(live);
    }

    /// <inheritdoc />
    public Task<RefreshSession?> FindByIdAsync(
        Guid id,
        UserIdentifierType userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.Find(s =>
            s.Id == id && EqualityComparer<UserIdentifierType>.Default.Equals(s.UserId, userId)));

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        var written = _staged.Count;
        _sessions.AddRange(_staged);
        _staged.Clear();
        return Task.FromResult(written);
    }

    /// <inheritdoc />
    public async Task<bool> TryRotateAsync(
        RefreshSession presented,
        RefreshSession successor,
        DateTime revokedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presented);
        ArgumentNullException.ThrowIfNull(successor);

        if (RotationOutcome is not null && !RotationOutcome())
        {
            return false;
        }

        if (presented.Revoke(revokedAt, RefreshSession.ReasonRotated, successor.TokenHash).IsFailure)
        {
            return false;
        }

        await AddAsync(successor, cancellationToken).ConfigureAwait(false);
        await SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
}
