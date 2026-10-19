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
/// The erasure read, against a real SQLite database: every row of the user, revoked and expired ones
/// included, none of another user's, and tracked so the anonymization the caller applies is saved.
/// </summary>
public sealed class EFRefreshSessionStoreGetAllByUserTests
{
    private const UserIdentifierType OwnerId = 42;
    private const UserIdentifierType OtherId = 43;

    private static readonly DateTime Now = new(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetAllByUserAsync_ReturnsEveryRowOfTheUser_RevokedAndExpiredIncluded()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(CancellationToken.None);
        await using ApplicationDbContext context = RefreshSessionCleanupServiceTests.SessionCleanupTestContext.Create(connection);
        var store = CreateStore(context);

        var active = await SeedAsync(context, OwnerId, "active", Now.AddDays(-1), Now.AddDays(6));
        var expired = await SeedAsync(context, OwnerId, "expired", Now.AddDays(-9), Now.AddDays(-2));
        var revoked = await SeedAsync(context, OwnerId, "revoked", Now.AddDays(-3), Now.AddDays(4), revoke: true);
        await SeedAsync(context, OtherId, "other", Now.AddDays(-1), Now.AddDays(6));

        var sessions = await store.GetAllByUserAsync(OwnerId, CancellationToken.None);

        sessions.Select(s => s.Id).Should().Equal(expired.Id, revoked.Id, active.Id);
    }

    [Fact]
    public async Task GetAllByUserAsync_ReturnsTrackedRows_SoAnAnonymizationPersists()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(CancellationToken.None);
        await using ApplicationDbContext context = RefreshSessionCleanupServiceTests.SessionCleanupTestContext.Create(connection);
        var store = CreateStore(context);
        await SeedAsync(context, OwnerId, "revoked", Now.AddDays(-3), Now.AddDays(4), revoke: true);

        foreach (var session in await store.GetAllByUserAsync(OwnerId, CancellationToken.None))
        {
            session.Anonymize();
        }

        await store.SaveChangesAsync(CancellationToken.None);
        context.ChangeTracker.Clear();

        var reread = (await store.GetAllByUserAsync(OwnerId, CancellationToken.None)).Should().ContainSingle().Subject;
        reread.IpAddress.Should().BeNull("a no-tracking read would drop the anonymization at save time");
        reread.UserAgent.Should().BeNull();
    }

    private static StoreUnderTest CreateStore(ApplicationDbContext context)
    {
        var dbContextFactory = new Mock<IDbContextFactory>();
        dbContextFactory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>())).Returns(context);
        dbContextFactory
            .Setup(f => f.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => context.SaveChangesAsync(ct));

        return new StoreUnderTest(
            dbContextFactory.Object,
            new EmptyEntityDataSourceRegistry(),
            Mock.Of<IDataSourceResolver>(),
            Options.Create(new RefreshSessionSettings { Enabled = true }));
    }

    private static async Task<RefreshSession> SeedAsync(
        ApplicationDbContext context,
        UserIdentifierType userId,
        string token,
        DateTime createdAt,
        DateTime expiresAt,
        bool revoke = false)
    {
        RefreshSession session = RefreshSession.Create(userId, token, createdAt, expiresAt, "203.0.113.7", "test-agent").Value!;
        if (revoke)
        {
            session.Revoke(Now, RefreshSession.ReasonSignedOut);
        }

        context.Add(session);
        await context.SaveChangesAsync(CancellationToken.None);
        context.ChangeTracker.Clear();
        return session;
    }
}
