using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
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
    public async Task EnsureCreatedAsync_WithNoSourcesInUse_CompletesSuccessfully()
    {
        await using var sut = CreateSut();

        var act = async () => await sut.EnsureCreatedAsync();

        await act.Should().NotThrowAsync();
    }

    // -- MigrateAsync --
    [Fact]
    public async Task MigrateAsync_WithNoSourcesInUse_CompletesSuccessfully()
    {
        await using var sut = CreateSut();

        var act = async () => await sut.MigrateAsync();

        await act.Should().NotThrowAsync();
    }

    // -- HasPendingMigrationsAsync --
    [Fact]
    public async Task HasPendingMigrationsAsync_WithNoSourcesInUse_ReturnsFalse()
    {
        await using var sut = CreateSut();

        var result = await sut.HasPendingMigrationsAsync();

        result.Should().BeFalse();
    }

    private static DbContextFactory CreateSut(Mock<IPhysicalDbContextFactory>? physicalFactory = null)
    {
        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);

        return new DbContextFactory(
            (physicalFactory ?? new Mock<IPhysicalDbContextFactory>()).Object,
            registry.Object,
            Mock.Of<IDataSourceResolver>(),
            Mock.Of<ICurrentUserService>());
    }
}
