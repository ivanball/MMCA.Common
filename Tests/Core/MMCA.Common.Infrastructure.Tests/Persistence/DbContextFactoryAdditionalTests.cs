#pragma warning disable CA2000 // Dispose objects before losing scope — test doubles do not hold real resources

using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class DbContextFactoryAdditionalTests
{
    // -- DisposeAsync --
    [Fact]
    public async Task DisposeAsync_AfterDispose_GetDbContext_ThrowsObjectDisposedException()
    {
        var sut = CreateSut();
        await sut.DisposeAsync();

        var act = () => sut.GetDbContext(DataSource.SQLServer);

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task DisposeAsync_Twice_IsIdempotent()
    {
        var sut = CreateSut();

        await sut.DisposeAsync();
        var act = async () => await sut.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    // -- RequestIdentityInsert --
    [Fact]
    public async Task RequestIdentityInsert_WithNoContexts_SaveReturnsZero()
    {
        await using var sut = CreateSut();

        sut.RequestIdentityInsert();
        var result = await sut.SaveChangesAsync();

        result.Should().Be(0);
    }

    // -- BeginTransaction / CommitTransaction / RollbackTransaction --
    [Fact]
    public void BeginTransaction_WithNoContexts_DoesNotThrow()
    {
        using var sut = CreateSut();

        var act = () => sut.BeginTransaction();

        act.Should().NotThrow();
    }

    [Fact]
    public void CommitTransaction_WithNoContexts_DoesNotThrow()
    {
        using var sut = CreateSut();

        var act = () => sut.CommitTransaction();

        act.Should().NotThrow();
    }

    [Fact]
    public void RollbackTransaction_WithNoContexts_DoesNotThrow()
    {
        using var sut = CreateSut();

        var act = () => sut.RollbackTransaction();

        act.Should().NotThrow();
    }

    // -- EnsureCreatedAsync --
    [Fact]
    public async Task EnsureCreatedAsync_WithNoContexts_CompletesSuccessfully()
    {
        await using var sut = CreateSut();

        var act = async () => await sut.EnsureCreatedAsync();

        await act.Should().NotThrowAsync();
    }

    // -- MigrateAsync --
    [Fact]
    public async Task MigrateAsync_WithNoContexts_CompletesSuccessfully()
    {
        await using var sut = CreateSut();

        var act = async () => await sut.MigrateAsync();

        await act.Should().NotThrowAsync();
    }

    // -- HasPendingMigrationsAsync --
    [Fact]
    public async Task HasPendingMigrationsAsync_WithNoContexts_ReturnsFalse()
    {
        await using var sut = CreateSut();

        var result = await sut.HasPendingMigrationsAsync();

        result.Should().BeFalse();
    }

    private static DbContextFactory CreateSut(
        Mock<IDbContextFactory<CosmosDbContext>>? cosmosFactory = null,
        Mock<IDbContextFactory<SqliteDbContext>>? sqliteFactory = null,
        Mock<IDbContextFactory<SQLServerDbContext>>? sqlServerFactory = null) =>
        new(
            (cosmosFactory ?? new Mock<IDbContextFactory<CosmosDbContext>>()).Object,
            (sqliteFactory ?? new Mock<IDbContextFactory<SqliteDbContext>>()).Object,
            (sqlServerFactory ?? new Mock<IDbContextFactory<SQLServerDbContext>>()).Object,
            Mock.Of<ICurrentUserService>());
}
