using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Services;

namespace MMCA.Common.Infrastructure.Tests.Services;

public class DataSourceServiceTests
{
    private readonly DataSourceService _sut = new();

    // ── GetDataSource(Type, Type) ──
    [Fact]
    public void GetDataSource_WithAttributeOnConfig_ReturnsDataSource()
    {
        var result = _sut.GetDataSource(typeof(FakeEntity), typeof(FakeSqlServerConfig));
        result.Should().Be(DataSource.SQLServer);
    }

    [Fact]
    public void GetDataSource_WithSqliteAttribute_ReturnsSqlite()
    {
        var result = _sut.GetDataSource(typeof(FakeEntity2), typeof(FakeSqliteConfig));
        result.Should().Be(DataSource.Sqlite);
    }

    [Fact]
    public void GetDataSource_WithoutAttribute_ThrowsInvalidOperationException() =>
        FluentActions.Invoking(() => _sut.GetDataSource(typeof(FakeEntity3), typeof(NoAttributeConfig)))
            .Should().Throw<InvalidOperationException>();

    // ── GetDataSource(string) ──
    [Fact]
    public void GetDataSource_ByName_AfterCaching_ReturnsDataSource()
    {
        _sut.GetDataSource(typeof(FakeEntity), typeof(FakeSqlServerConfig));
        var result = _sut.GetDataSource(typeof(FakeEntity).FullName!);
        result.Should().Be(DataSource.SQLServer);
    }

    [Fact]
    public void GetDataSource_ByName_WhenNotCached_ThrowsInvalidOperationException() =>
        FluentActions.Invoking(() => _sut.GetDataSource("NonExistent.Entity"))
            .Should().Throw<InvalidOperationException>();

    // ── GetDataSource(Type) ──
    [Fact]
    public void GetDataSource_ByType_AfterCaching_ReturnsDataSource()
    {
        _sut.GetDataSource(typeof(FakeEntity), typeof(FakeSqlServerConfig));
        var result = _sut.GetDataSource(typeof(FakeEntity));
        result.Should().Be(DataSource.SQLServer);
    }

    // ── Caching ──
    [Fact]
    public void GetDataSource_CachesResult_ReturnsSameOnSecondCall()
    {
        var first = _sut.GetDataSource(typeof(FakeEntity), typeof(FakeSqlServerConfig));
        var second = _sut.GetDataSource(typeof(FakeEntity), typeof(FakeSqlServerConfig));
        first.Should().Be(second);
    }

    // ── HaveIncludeSupport ──
    [Fact]
    public void HaveIncludeSupport_SameNonCosmos_ReturnsTrue() =>
        _sut.HaveIncludeSupport(DataSource.SQLServer, DataSource.SQLServer).Should().BeTrue();

    [Fact]
    public void HaveIncludeSupport_SameSqlite_ReturnsTrue() =>
        _sut.HaveIncludeSupport(DataSource.Sqlite, DataSource.Sqlite).Should().BeTrue();

    [Fact]
    public void HaveIncludeSupport_DifferentDataSources_ReturnsFalse() =>
        _sut.HaveIncludeSupport(DataSource.SQLServer, DataSource.Sqlite).Should().BeFalse();

    [Fact]
    public void HaveIncludeSupport_BothCosmos_ReturnsFalse() =>
        _sut.HaveIncludeSupport(DataSource.CosmosDB, DataSource.CosmosDB).Should().BeFalse();

    // ── HaveIncludeSupport(string, string) ──
    [Fact]
    public void HaveIncludeSupport_ByName_SameDataSource_ReturnsTrue()
    {
        _sut.GetDataSource(typeof(FakeEntity), typeof(FakeSqlServerConfig));
        _sut.GetDataSource(typeof(FakeEntity2), typeof(FakeSqlServerConfig2));

        _sut.HaveIncludeSupport(
            typeof(FakeEntity).FullName!,
            typeof(FakeEntity2).FullName!).Should().BeTrue();
    }

    [Fact]
    public void HaveIncludeSupport_ByName_FirstNotCached_ReturnsFalse() =>
        _sut.HaveIncludeSupport("NonExistent.Entity1", "NonExistent.Entity2").Should().BeFalse();

    [Fact]
    public void HaveIncludeSupport_ByName_DifferentDataSources_ReturnsFalse()
    {
        _sut.GetDataSource(typeof(FakeEntity), typeof(FakeSqlServerConfig));
        _sut.GetDataSource(typeof(FakeEntity2), typeof(FakeSqliteConfig));

        _sut.HaveIncludeSupport(
            typeof(FakeEntity).FullName!,
            typeof(FakeEntity2).FullName!).Should().BeFalse();
    }

    [Fact]
    public void HaveIncludeSupport_ByName_SecondNotCached_ReturnsFalse()
    {
        _sut.GetDataSource(typeof(FakeEntity), typeof(FakeSqlServerConfig));

        _sut.HaveIncludeSupport(
            typeof(FakeEntity).FullName!,
            "NonExistent.Entity").Should().BeFalse();
    }

    // ── Test types ──
    private sealed class FakeEntity { }

    private sealed class FakeEntity2 { }

    private sealed class FakeEntity3 { }

    [UseDataSource(DataSource.SQLServer)]
    private sealed class FakeSqlServerConfig { }

    [UseDataSource(DataSource.SQLServer)]
    private sealed class FakeSqlServerConfig2 { }

    [UseDataSource(DataSource.Sqlite)]
    private sealed class FakeSqliteConfig { }

    private sealed class NoAttributeConfig { }
}
