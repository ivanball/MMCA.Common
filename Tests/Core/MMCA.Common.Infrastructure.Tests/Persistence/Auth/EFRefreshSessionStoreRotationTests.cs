using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    // T13: the successor's SignInId is an ordinary column, persisted by the insert the claim stages,
    // so a rotation chain read back from the database still groups into one sign-in.
    [Fact]
    public async Task TryRotateAsync_PersistsTheSuccessorsSignIn_SoTheChainReadsBackAsOneSignIn()
    {
        await using var harness = await RotationHarness.CreateAsync();
        RefreshSession seeded = await harness.SeedAsync("presented");
        Participant writer = harness.NewParticipant();

        RefreshSession presented = (await writer.Store.FindByTokenHashAsync(seeded.TokenHash, CancellationToken.None))!;
        RefreshSession successor = presented.CreateSuccessor("successor", Now.AddMinutes(15), Now.AddDays(7)).Value!;

        (await writer.Store.TryRotateAsync(presented, successor, Now.AddMinutes(15), CancellationToken.None)).Should().BeTrue();

        RefreshSession[] rows = await harness.ReadAllAsync();
        rows.Should().HaveCount(2).And.OnlyContain(r => r.SignInId == seeded.Id);
    }

    // T13 review: the history is aggregated IN the database. A sign-in rotated 120 times plus legacy
    // rows must cost two statements and one materialized entity per sign-in, never one per row.
    [Fact]
    public async Task GetRecentSignInsAsync_FoldsA120RowChainInTheDatabase_MaterializingOneHeadPerSignIn()
    {
        await using var harness = await RotationHarness.CreateAsync();
        var chain = RotationChain(OwnerId, "owner", Now, rows: 120);
        var legacyRotated = Legacy(OwnerId, "legacy-rotated", Now.AddDays(-3));
        legacyRotated.Revoke(Now.AddDays(-2), RefreshSession.ReasonRotated, RefreshSession.HashToken("legacy-live"));
        var legacyLive = Legacy(OwnerId, "legacy-live", Now.AddDays(-2));
        await harness.SeedAllAsync([.. chain, legacyRotated, legacyLive, .. RotationChain(7, "other", Now, rows: 3)]);

        var counter = new QueryCounter();
        Participant reader = harness.NewParticipant(counter);
        counter.Reset();

        var signIns = await reader.Store.GetRecentSignInsAsync(OwnerId, 50, CancellationToken.None);

        signIns.Select(s => s.SignInId).Should().Equal(chain[0].Id, legacyLive.Id);
        signIns[0].StartedAt.Should().Be(chain[0].CreatedAt);
        signIns[0].Head.Id.Should().Be(chain[^1].Id, "the head is the chain's newest row");
        signIns[1].Head.Id.Should().Be(legacyLive.Id, "a rotated legacy row is represented by its successor");
        counter.Materialized.Should().Be(2, "one head per sign-in, not 120 chain rows");
        counter.Commands.Should().HaveCount(3, "one grouped statement, one head lookup, one legacy read");
        reader.Context.ChangeTracker.Entries().Should().BeEmpty("a history read is untracked");
    }

    [Fact]
    public async Task GetRecentSignInsAsync_KeepsOnlyTheNewestSignIns()
    {
        await using var harness = await RotationHarness.CreateAsync();
        List<RefreshSession> rows = [];
        for (var i = 0; i < 5; i++)
        {
            rows.AddRange(RotationChain(OwnerId, string.Create(CultureInfo.InvariantCulture, $"chain-{i}"), Now.AddHours(i), rows: 2));
        }

        await harness.SeedAllAsync(rows);
        var counter = new QueryCounter();
        Participant reader = harness.NewParticipant(counter);
        counter.Reset();

        var signIns = await reader.Store.GetRecentSignInsAsync(OwnerId, 3, CancellationToken.None);

        signIns.Select(s => s.StartedAt).Should().Equal(Now.AddHours(4), Now.AddHours(3), Now.AddHours(2));
        counter.Materialized.Should().Be(3, "the cap is applied in the database, before materialization");
    }

    [Fact]
    public void SignInQueries_TranslateToGroupedAndBoundedStatements()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = RefreshSessionCleanupServiceTests.SessionCleanupTestContext.Create(connection);
        var sessions = context.Set<RefreshSession>().AsNoTracking();

        var chains = StoreUnderTest.SignInChainsQuery(sessions, OwnerId, 50).ToQueryString();
        var heads = StoreUnderTest.SignInHeadsQuery(sessions, OwnerId, [Guid.NewGuid(), Guid.NewGuid()], [Now, Now.AddMinutes(1)]).ToQueryString();
        var legacy = StoreUnderTest.LegacySignInsQuery(sessions, OwnerId, 50).ToQueryString();
        TestContext.Current.TestOutputHelper?.WriteLine(chains);
        TestContext.Current.TestOutputHelper?.WriteLine(heads);
        TestContext.Current.TestOutputHelper?.WriteLine(legacy);

        chains.Should().Contain("GROUP BY").And.Contain("MIN(").And.Contain("MAX(").And.Contain("LIMIT");
        chains.Should().NotContain("\"Id\"", "the grouped statement returns scalars, never whole rows");
        heads.Should().Contain("IN (").And.NotContain("GROUP BY");
        legacy.Should().Contain("IS NULL").And.Contain("LIMIT");
    }

    /// <summary>A sign-in of <paramref name="rows"/> sessions, each rotated into the next one minute later.</summary>
    private static List<RefreshSession> RotationChain(UserIdentifierType userId, string tokenPrefix, DateTime start, int rows)
    {
        List<RefreshSession> chain = [RefreshSession.Create(userId, $"{tokenPrefix}-0", start, start.AddDays(7)).Value!];
        for (var i = 1; i < rows; i++)
        {
            var createdAt = start.AddMinutes(i);
            var token = string.Create(CultureInfo.InvariantCulture, $"{tokenPrefix}-{i}");
            var successor = chain[^1].CreateSuccessor(token, createdAt, createdAt.AddDays(7)).Value!;
            chain[^1].Revoke(createdAt, RefreshSession.ReasonRotated, successor.TokenHash);
            chain.Add(successor);
        }

        return chain;
    }

    private static RefreshSession Legacy(UserIdentifierType userId, string token, DateTime createdAt) =>
        new()
        {
            UserId = userId,
            TokenHash = RefreshSession.HashToken(token),
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddDays(7),
        };

    /// <summary>Counts the entities EF materializes and the commands it sends.</summary>
    private sealed class QueryCounter : DbCommandInterceptor, IMaterializationInterceptor
    {
        public int Materialized { get; private set; }

        public List<string> Commands { get; } = [];

        /// <summary>Forgets what context creation (EnsureCreated) did, so only the call under test counts.</summary>
        public void Reset()
        {
            Materialized = 0;
            Commands.Clear();
        }

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is RefreshSession)
            {
                Materialized++;
            }

            return entity;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
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

        public Participant NewParticipant(params IInterceptor[] interceptors)
        {
            ApplicationDbContext context =
                RefreshSessionCleanupServiceTests.SessionCleanupTestContext.Create(_connection, interceptors);
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

        public async Task SeedAllAsync(IEnumerable<RefreshSession> sessions)
        {
            Participant seeder = NewParticipant();
            seeder.Context.AddRange(sessions);
            await seeder.Context.SaveChangesAsync(CancellationToken.None);
            seeder.Context.ChangeTracker.Clear();
        }

        public Task<RefreshSession[]> ReadAllAsync()
        {
            Participant reader = NewParticipant();
            return reader.Context.Set<RefreshSession>()
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
