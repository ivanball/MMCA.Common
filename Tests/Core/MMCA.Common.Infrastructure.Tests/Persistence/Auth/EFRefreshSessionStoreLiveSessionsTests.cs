using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;
using StoreUnderTest = MMCA.Common.Infrastructure.Persistence.Auth.EFRefreshSessionStore;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Auth;

/// <summary>
/// The two roster queries behind a consumer's "signed in" filter and column, asserted against a
/// real SQLite database: the live-session predicate (not revoked, <c>ExpiresAt</c> strictly after
/// the caller's now, the same rule as <see cref="RefreshSession.IsActiveAt"/>) has to translate and
/// run in the store, so an in-memory double would prove nothing.
/// </summary>
public sealed class EFRefreshSessionStoreLiveSessionsTests
{
    private const UserIdentifierType UserA = 42;
    private const UserIdentifierType UserB = 43;
    private const UserIdentifierType UserC = 44;
    private const UserIdentifierType UserD = 45;

    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    // ── GetUserIdsWithLiveSessionsAsync ──
    [Fact]
    public async Task GetUserIdsWithLiveSessions_ReturnsOnlyUsersWithALiveSession()
    {
        await using var harness = await StoreHarness.CreateAsync();
        await harness.SeedAsync(UserA, "a-live", expiresAt: Now.AddDays(1));
        await harness.SeedAsync(UserB, "b-revoked", expiresAt: Now.AddDays(1), revoked: true);
        await harness.SeedAsync(UserC, "c-expired", expiresAt: Now.AddDays(-1));

        var ids = await harness.Store.GetUserIdsWithLiveSessionsAsync(Now, CancellationToken.None);

        ids.Should().BeEquivalentTo(
            [UserA],
            "a revoked session and an expired one are not signed in");
    }

    [Fact]
    public async Task GetUserIdsWithLiveSessions_ReturnsEachUserOnce_WhateverTheirSessionCount()
    {
        await using var harness = await StoreHarness.CreateAsync();
        await harness.SeedAsync(UserA, "a-phone", expiresAt: Now.AddDays(1));
        await harness.SeedAsync(UserA, "a-laptop", expiresAt: Now.AddDays(2));
        await harness.SeedAsync(UserA, "a-tablet", expiresAt: Now.AddDays(3));
        await harness.SeedAsync(UserB, "b-phone", expiresAt: Now.AddDays(1));

        var ids = await harness.Store.GetUserIdsWithLiveSessionsAsync(Now, CancellationToken.None);

        ids.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        ids.Should().BeEquivalentTo([UserA, UserB]);
    }

    [Fact]
    public async Task GetUserIdsWithLiveSessions_AUserWithOneLiveAmongDeadSessions_IsSignedIn()
    {
        await using var harness = await StoreHarness.CreateAsync();
        await harness.SeedAsync(UserA, "a-revoked", expiresAt: Now.AddDays(1), revoked: true);
        await harness.SeedAsync(UserA, "a-expired", expiresAt: Now.AddDays(-2));
        await harness.SeedAsync(UserA, "a-live", expiresAt: Now.AddHours(1));

        var ids = await harness.Store.GetUserIdsWithLiveSessionsAsync(Now, CancellationToken.None);

        ids.Should().Equal(UserA);
    }

    [Fact]
    public async Task GetUserIdsWithLiveSessions_ASessionExpiringExactlyNow_IsNotLive()
    {
        await using var harness = await StoreHarness.CreateAsync();
        await harness.SeedAsync(UserA, "a-boundary", expiresAt: Now);

        var ids = await harness.Store.GetUserIdsWithLiveSessionsAsync(Now, CancellationToken.None);

        ids.Should().BeEmpty("IsActiveAt requires ExpiresAt strictly after now");
    }

    [Fact]
    public async Task GetUserIdsWithLiveSessions_UsesTheCallersNow_NotTheWallClock()
    {
        await using var harness = await StoreHarness.CreateAsync();
        await harness.SeedAsync(UserA, "a-short", expiresAt: Now.AddDays(1));
        await harness.SeedAsync(UserB, "b-long", expiresAt: Now.AddDays(10));

        var ids = await harness.Store.GetUserIdsWithLiveSessionsAsync(Now.AddDays(5), CancellationToken.None);

        ids.Should().Equal(UserB);
    }

    [Fact]
    public async Task GetUserIdsWithLiveSessions_WithNoSessions_IsEmpty()
    {
        await using var harness = await StoreHarness.CreateAsync();

        var ids = await harness.Store.GetUserIdsWithLiveSessionsAsync(Now, CancellationToken.None);

        ids.Should().BeEmpty();
    }

    // ── CountLiveSessionsByUserAsync ──
    [Fact]
    public async Task CountLiveSessionsByUser_CountsOnlyLiveSessions_PerRequestedUser()
    {
        await using var harness = await StoreHarness.CreateAsync();
        await harness.SeedAsync(UserA, "a-1", expiresAt: Now.AddDays(1));
        await harness.SeedAsync(UserA, "a-2", expiresAt: Now.AddDays(2));
        await harness.SeedAsync(UserA, "a-revoked", expiresAt: Now.AddDays(2), revoked: true);
        await harness.SeedAsync(UserA, "a-expired", expiresAt: Now.AddDays(-1));
        await harness.SeedAsync(UserB, "b-1", expiresAt: Now.AddDays(1));

        var counts = await harness.Store.CountLiveSessionsByUserAsync([UserA, UserB], Now, CancellationToken.None);

        counts.Should().BeEquivalentTo(new Dictionary<UserIdentifierType, int> { [UserA] = 2, [UserB] = 1 });
    }

    [Fact]
    public async Task CountLiveSessionsByUser_AUserWithNoLiveSession_IsAbsent()
    {
        await using var harness = await StoreHarness.CreateAsync();
        await harness.SeedAsync(UserA, "a-1", expiresAt: Now.AddDays(1));
        await harness.SeedAsync(UserB, "b-revoked", expiresAt: Now.AddDays(1), revoked: true);
        await harness.SeedAsync(UserC, "c-expired", expiresAt: Now.AddDays(-1));

        var counts = await harness.Store.CountLiveSessionsByUserAsync(
            [UserA, UserB, UserC, UserD], Now, CancellationToken.None);

        counts.Should().BeEquivalentTo(new Dictionary<UserIdentifierType, int> { [UserA] = 1 });
        counts.Should().NotContainKeys(
            [UserB, UserC, UserD],
            "a requested user with only dead sessions, or none at all, is absent rather than a zero entry");
    }

    [Fact]
    public async Task CountLiveSessionsByUser_OnlyCountsTheRequestedUsers()
    {
        await using var harness = await StoreHarness.CreateAsync();
        await harness.SeedAsync(UserA, "a-1", expiresAt: Now.AddDays(1));
        await harness.SeedAsync(UserB, "b-1", expiresAt: Now.AddDays(1));
        await harness.SeedAsync(UserB, "b-2", expiresAt: Now.AddDays(1));

        var counts = await harness.Store.CountLiveSessionsByUserAsync([UserA], Now, CancellationToken.None);

        counts.Should().BeEquivalentTo(new Dictionary<UserIdentifierType, int> { [UserA] = 1 });
    }

    [Fact]
    public async Task CountLiveSessionsByUser_DuplicateRequestedIds_CountOnce()
    {
        await using var harness = await StoreHarness.CreateAsync();
        await harness.SeedAsync(UserA, "a-1", expiresAt: Now.AddDays(1));
        await harness.SeedAsync(UserA, "a-2", expiresAt: Now.AddDays(1));

        var counts = await harness.Store.CountLiveSessionsByUserAsync([UserA, UserA, UserA], Now, CancellationToken.None);

        counts.Should().BeEquivalentTo(
            new Dictionary<UserIdentifierType, int> { [UserA] = 2 },
            "naming a user twice must not double their sessions");
    }

    [Fact]
    public async Task CountLiveSessionsByUser_UsesTheCallersNow()
    {
        await using var harness = await StoreHarness.CreateAsync();
        await harness.SeedAsync(UserA, "a-short", expiresAt: Now.AddDays(1));
        await harness.SeedAsync(UserA, "a-long", expiresAt: Now.AddDays(10));

        var counts = await harness.Store.CountLiveSessionsByUserAsync([UserA], Now.AddDays(5), CancellationToken.None);

        counts.Should().BeEquivalentTo(new Dictionary<UserIdentifierType, int> { [UserA] = 1 });
    }

    [Fact]
    public async Task CountLiveSessionsByUser_NoRequestedUsers_IsEmpty()
    {
        await using var harness = await StoreHarness.CreateAsync();
        await harness.SeedAsync(UserA, "a-1", expiresAt: Now.AddDays(1));

        var counts = await harness.Store.CountLiveSessionsByUserAsync([], Now, CancellationToken.None);

        counts.Should().BeEmpty();
    }

    private sealed class StoreHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private StoreHarness(SqliteConnection connection, ApplicationDbContext context, IRefreshSessionStore store)
        {
            _connection = connection;
            Context = context;
            Store = store;
        }

        public ApplicationDbContext Context { get; }

        public IRefreshSessionStore Store { get; }

        public static async Task<StoreHarness> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(CancellationToken.None);
            ApplicationDbContext context =
                RefreshSessionCleanupServiceTests.SessionCleanupTestContext.Create(connection);

            var dbContextFactory = new Mock<IDbContextFactory>();
            dbContextFactory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>())).Returns(context);

            var store = new StoreUnderTest(
                dbContextFactory.Object,
                new EmptyEntityDataSourceRegistry(),
                Mock.Of<IDataSourceResolver>(),
                Options.Create(new RefreshSessionSettings { Enabled = true }));

            return new StoreHarness(connection, context, store);
        }

        public async Task SeedAsync(UserIdentifierType userId, string token, DateTime expiresAt, bool revoked = false)
        {
            RefreshSession session = RefreshSession.Create(userId, token, Now.AddDays(-30), expiresAt).Value!;
            if (revoked)
            {
                session.Revoke(Now.AddMinutes(-5), RefreshSession.ReasonSignedOut);
            }

            Context.Add(session);
            await Context.SaveChangesAsync(CancellationToken.None);
            Context.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
