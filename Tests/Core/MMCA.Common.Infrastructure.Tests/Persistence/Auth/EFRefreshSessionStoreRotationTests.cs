using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;
using StoreUnderTest = MMCA.Common.Infrastructure.Persistence.Auth.EFRefreshSessionStore;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Auth;

/// <summary>
/// The rotation claim (H35), asserted against a real SQLite database because the guarantee is the
/// database's, not the change tracker's: two requests presenting the SAME still-live refresh token
/// each load their own tracked copy with <c>RevokedAt</c> null, so an in-memory check-then-act lets
/// both mint a successor and leaves the presented row unable to ever fire reuse detection again.
/// <para>
/// Two stores over two contexts sharing ONE open connection are exactly that pair of requests: each
/// has its own change tracker, and the row they contend for is the same row.
/// </para>
/// </summary>
public sealed class EFRefreshSessionStoreRotationTests
{
    private const UserIdentifierType OwnerId = 42;

    private static readonly DateTime Now = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TryRotateAsync_WhenTheRowIsStillLive_ClaimsItAndPersistsTheSuccessor()
    {
        await using var harness = await RotationHarness.CreateAsync();
        RefreshSession seeded = await harness.SeedAsync("presented");
        Participant writer = harness.NewParticipant();

        RefreshSession presented = (await writer.Store.FindByTokenHashAsync(seeded.TokenHash, CancellationToken.None))!;
        RefreshSession successor = NewSession("successor");

        var claimed = await writer.Store.TryRotateAsync(presented, successor, Now, CancellationToken.None);

        claimed.Should().BeTrue();
        presented.IsRevoked.Should().BeTrue("the tracked instance mirrors the row the claim just wrote");
        presented.ReasonRevoked.Should().Be(RefreshSession.ReasonRotated);
        presented.ReplacedByTokenHash.Should().Be(successor.TokenHash);
        writer.Context.ChangeTracker.HasChanges().Should().BeFalse(
            "the claim was written by the conditional UPDATE, so the tracked row must not be re-issued as a modification");

        RefreshSession[] rows = await harness.ReadAllAsync();
        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(r => r.Id == successor.Id && !r.IsRevoked);
        rows.Should().ContainSingle(r =>
            r.Id == presented.Id && r.IsRevoked && r.ReplacedByTokenHash == successor.TokenHash);
    }

    [Fact]
    public async Task TryRotateAsync_WhenAnotherWriterAlreadyClaimedTheRow_ReturnsFalseAndWritesNothing()
    {
        await using var harness = await RotationHarness.CreateAsync();
        RefreshSession seeded = await harness.SeedAsync("presented");

        // Both requests read the row while it is still live, exactly as two concurrent refreshes do.
        Participant winner = harness.NewParticipant();
        Participant loser = harness.NewParticipant();
        RefreshSession winnersCopy = (await winner.Store.FindByTokenHashAsync(seeded.TokenHash, CancellationToken.None))!;
        RefreshSession losersCopy = (await loser.Store.FindByTokenHashAsync(seeded.TokenHash, CancellationToken.None))!;

        RefreshSession winnersSuccessor = NewSession("winner-successor");
        RefreshSession losersSuccessor = NewSession("loser-successor");

        var winnerClaimed = await winner.Store.TryRotateAsync(
            winnersCopy, winnersSuccessor, Now, CancellationToken.None);
        var loserClaimed = await loser.Store.TryRotateAsync(
            losersCopy, losersSuccessor, Now.AddSeconds(1), CancellationToken.None);

        winnerClaimed.Should().BeTrue();
        loserClaimed.Should().BeFalse(
            "the row is claimed by a conditional UPDATE, so only one of two concurrent rotations may win");

        RefreshSession[] rows = await harness.ReadAllAsync();
        rows.Should().HaveCount(2, "the loser must not mint a second successor from one presented token");
        rows.Should().NotContain(r => r.Id == losersSuccessor.Id);
        RefreshSession rotated = rows.Single(r => r.Id == seeded.Id);
        rotated.ReplacedByTokenHash.Should().Be(
            winnersSuccessor.TokenHash,
            "the rotation chain records the winner, and the loser overwrites nothing");
        rotated.RevokedAt.Should().Be(Now);
    }

    private static RefreshSession NewSession(string token) =>
        RefreshSession.Create(OwnerId, token, Now, Now.AddDays(7)).Value!;

    private sealed record Participant(ApplicationDbContext Context, IRefreshSessionStore Store);

    /// <summary>
    /// One SQLite in-memory database, several independent contexts over it. Each participant is a
    /// separate request: its own change tracker, its own store, the same rows.
    /// </summary>
    private sealed class RotationHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly List<ApplicationDbContext> _contexts = [];

        private RotationHarness(SqliteConnection connection) => _connection = connection;

        public static async Task<RotationHarness> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(CancellationToken.None);
            return new RotationHarness(connection);
        }

        public Participant NewParticipant()
        {
            ApplicationDbContext context =
                RefreshSessionCleanupServiceTests.SessionCleanupTestContext.Create(_connection);
            _contexts.Add(context);

            var dbContextFactory = new Mock<IDbContextFactory>();
            dbContextFactory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>())).Returns(context);
            dbContextFactory
                .Setup(f => f.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken ct) => context.SaveChangesAsync(ct));

            // The transaction belongs to the factory in production; here the delegate runs directly,
            // so the assertions are about the claim itself rather than about SQLite locking.
            dbContextFactory
                .Setup(f => f.ExecuteInTransactionAsync(
                    It.IsAny<Func<CancellationToken, Task<bool>>>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Func<CancellationToken, Task<bool>> operation, CancellationToken ct) => operation(ct));

            var store = new StoreUnderTest(
                dbContextFactory.Object,
                new EmptyEntityDataSourceRegistry(),
                Mock.Of<IDataSourceResolver>(),
                Options.Create(new RefreshSessionSettings { Enabled = true }));

            return new Participant(context, store);
        }

        public async Task<RefreshSession> SeedAsync(string token)
        {
            Participant seeder = NewParticipant();
            RefreshSession session = NewSession(token);
            seeder.Context.Add(session);
            await seeder.Context.SaveChangesAsync(CancellationToken.None);
            seeder.Context.ChangeTracker.Clear();
            return session;
        }

        public async Task<RefreshSession[]> ReadAllAsync()
        {
            Participant reader = NewParticipant();
            return await reader.Context.Set<RefreshSession>()
                .AsNoTracking()
                .ToArrayAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (ApplicationDbContext context in _contexts)
            {
                await context.DisposeAsync();
            }

            await _connection.DisposeAsync();
        }
    }
}
