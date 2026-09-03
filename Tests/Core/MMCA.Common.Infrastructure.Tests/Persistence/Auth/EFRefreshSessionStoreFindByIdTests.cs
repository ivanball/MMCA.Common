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
/// The ownership-scoped lookup behind per-device sign-out, asserted against a real SQLite database
/// rather than an in-memory double: the security property (another account's session id is
/// unreadable, not merely rejected after being read) lives in the EF predicate itself.
/// </summary>
public sealed class EFRefreshSessionStoreFindByIdTests
{
    private const UserIdentifierType OwnerId = 42;
    private const UserIdentifierType OtherId = 43;

    private static readonly DateTime Now = new(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FindByIdAsync_OwnedSession_ReturnsIt()
    {
        await using var harness = await StoreHarness.CreateAsync();
        RefreshSession session = await harness.SeedAsync(OwnerId, "a");

        RefreshSession? found = await harness.Store.FindByIdAsync(session.Id, OwnerId, CancellationToken.None);

        found.Should().NotBeNull();
        found!.Id.Should().Be(session.Id);
    }

    [Fact]
    public async Task FindByIdAsync_AnotherUsersSession_ReturnsNull()
    {
        await using var harness = await StoreHarness.CreateAsync();
        RefreshSession theirs = await harness.SeedAsync(OtherId, "a");

        RefreshSession? found = await harness.Store.FindByIdAsync(theirs.Id, OwnerId, CancellationToken.None);

        found.Should().BeNull("the owner is part of the query, so another account's id reads as nothing at all");
    }

    [Fact]
    public async Task FindByIdAsync_UnknownId_ReturnsNull()
    {
        await using var harness = await StoreHarness.CreateAsync();

        RefreshSession? found = await harness.Store.FindByIdAsync(Guid.NewGuid(), OwnerId, CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task FindByIdAsync_RevokedSession_StillReturnsIt()
    {
        await using var harness = await StoreHarness.CreateAsync();
        RefreshSession session = await harness.SeedAsync(OwnerId, "a", revoked: true);

        RefreshSession? found = await harness.Store.FindByIdAsync(session.Id, OwnerId, CancellationToken.None);

        found.Should().NotBeNull(
            "no query filter may hide a revoked row: the caller decides what a revoked session means");
        found!.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task FindByIdAsync_ReturnsATrackedInstance_SoARevocationPersists()
    {
        await using var harness = await StoreHarness.CreateAsync();
        RefreshSession session = await harness.SeedAsync(OwnerId, "a");

        RefreshSession found = (await harness.Store.FindByIdAsync(session.Id, OwnerId, CancellationToken.None))!;
        found.Revoke(Now, RefreshSession.ReasonSignedOut);
        await harness.Store.SaveChangesAsync(CancellationToken.None);

        harness.Context.ChangeTracker.Clear();
        RefreshSession reread = (await harness.Store.FindByIdAsync(session.Id, OwnerId, CancellationToken.None))!;
        reread.IsRevoked.Should().BeTrue(
            "a no-tracking read would take the revocation and drop it silently at save time");
        reread.ReasonRevoked.Should().Be(RefreshSession.ReasonSignedOut);
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
            dbContextFactory
                .Setup(f => f.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken ct) => context.SaveChangesAsync(ct));

            var store = new StoreUnderTest(
                dbContextFactory.Object,
                new EmptyEntityDataSourceRegistry(),
                Mock.Of<IDataSourceResolver>(),
                Options.Create(new RefreshSessionSettings { Enabled = true }));

            return new StoreHarness(connection, context, store);
        }

        public async Task<RefreshSession> SeedAsync(UserIdentifierType userId, string token, bool revoked = false)
        {
            RefreshSession session = RefreshSession.Create(userId, token, Now.AddDays(-1), Now.AddDays(6)).Value!;
            if (revoked)
            {
                session.Revoke(Now, RefreshSession.ReasonSignedOut);
            }

            Context.Add(session);
            await Context.SaveChangesAsync(CancellationToken.None);
            Context.ChangeTracker.Clear();
            return session;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
