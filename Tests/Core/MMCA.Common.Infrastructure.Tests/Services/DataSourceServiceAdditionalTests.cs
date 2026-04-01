using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Services;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class DataSourceServiceAdditionalTests
{
    private readonly DataSourceService _sut = new();

    // ── GetDataSource generic method ──
    [Fact]
    public void GetDataSource_GenericMethod_ReturnsDataSource()
    {
        // First call caches via the two-type overload
        _sut.GetDataSource(typeof(FakeEntity), typeof(FakeSqlServerConfig));

        // Generic method delegates to GetDataSource(Type, Type)
        var result = _sut.GetDataSource(typeof(FakeEntity));
        result.Should().Be(DataSource.SQLServer);
    }

    // ── HaveIncludeSupport(string, string) edge cases ──
    [Fact]
    public void HaveIncludeSupport_ByName_FirstNotCached_ReturnsFalse() =>
        _sut.HaveIncludeSupport("Not.Cached.Entity", typeof(FakeEntity).FullName!)
            .Should().BeFalse();

    [Fact]
    public void HaveIncludeSupport_ByName_SecondNotCached_ReturnsFalse()
    {
        _sut.GetDataSource(typeof(FakeEntity), typeof(FakeSqlServerConfig));

        _sut.HaveIncludeSupport(typeof(FakeEntity).FullName!, "Not.Cached.Entity")
            .Should().BeFalse();
    }

    [Fact]
    public void HaveIncludeSupport_ByName_BothCosmos_ReturnsFalse()
    {
        _sut.GetDataSource(typeof(FakeCosmosEntity1), typeof(FakeCosmosConfig1));
        _sut.GetDataSource(typeof(FakeCosmosEntity2), typeof(FakeCosmosConfig2));

        _sut.HaveIncludeSupport(
            typeof(FakeCosmosEntity1).FullName!,
            typeof(FakeCosmosEntity2).FullName!).Should().BeFalse();
    }

    [Fact]
    public void HaveIncludeSupport_ByName_DifferentDataSources_ReturnsFalse()
    {
        _sut.GetDataSource(typeof(FakeEntity), typeof(FakeSqlServerConfig));
        _sut.GetDataSource(typeof(FakeSqliteEntity), typeof(FakeSqliteConfig));

        _sut.HaveIncludeSupport(
            typeof(FakeEntity).FullName!,
            typeof(FakeSqliteEntity).FullName!).Should().BeFalse();
    }

    // ── GetDataSource(Type) when not cached throws ──
    [Fact]
    public void GetDataSource_ByType_WhenNotCached_ThrowsInvalidOperationException() =>
        FluentActions.Invoking(() => _sut.GetDataSource(typeof(UncachedEntity)))
            .Should().Throw<InvalidOperationException>();

    // ── HaveIncludeSupport with enum values ──
    [Fact]
    public void HaveIncludeSupport_SameSqlite_ReturnsTrue_ByName()
    {
        _sut.GetDataSource(typeof(FakeSqliteEntity), typeof(FakeSqliteConfig));
        _sut.GetDataSource(typeof(FakeSqliteEntity2), typeof(FakeSqliteConfig2));

        _sut.HaveIncludeSupport(
            typeof(FakeSqliteEntity).FullName!,
            typeof(FakeSqliteEntity2).FullName!).Should().BeTrue();
    }

    // ── Test types ──
    private sealed class FakeEntity { }

    private sealed class FakeSqliteEntity { }

    private sealed class FakeSqliteEntity2 { }

    private sealed class FakeCosmosEntity1 { }

    private sealed class FakeCosmosEntity2 { }

    private sealed class UncachedEntity { }

    [UseDataSource(DataSource.SQLServer)]
    private sealed class FakeSqlServerConfig { }

    [UseDataSource(DataSource.Sqlite)]
    private sealed class FakeSqliteConfig { }

    [UseDataSource(DataSource.Sqlite)]
    private sealed class FakeSqliteConfig2 { }

    [UseDataSource(DataSource.CosmosDB)]
    private sealed class FakeCosmosConfig1 { }

    [UseDataSource(DataSource.CosmosDB)]
    private sealed class FakeCosmosConfig2 { }
}
