using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Services;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Services;

/// <summary>
/// Tests for the <see cref="DataSourceService"/> facade over <see cref="IEntityDataSourceRegistry"/>:
/// key pass-through, engine extraction, and include-support classification.
/// </summary>
public sealed class DataSourceServiceTests
{
    private readonly Mock<IEntityDataSourceRegistry> _registry = new();
    private readonly DataSourceService _sut;

    public DataSourceServiceTests() => _sut = new DataSourceService(_registry.Object);

    // ── GetDataSourceKey(Type): pass-through ──
    [Fact]
    public void GetDataSourceKey_ByType_DelegatesToRegistry()
    {
        var key = new DataSourceKey(DataSource.SQLServer, "Sales");
        _registry.Setup(r => r.GetDataSourceKey(typeof(FakeEntity))).Returns(key);

        var result = _sut.GetDataSourceKey(typeof(FakeEntity));

        result.Should().Be(key);
    }

    // ── GetDataSourceKey(string): pass-through ──
    [Fact]
    public void GetDataSourceKey_ByName_DelegatesToRegistry()
    {
        var key = new DataSourceKey(DataSource.Sqlite, "Conference");
        _registry.Setup(r => r.GetDataSourceKey(typeof(FakeEntity).FullName!)).Returns(key);

        var result = _sut.GetDataSourceKey(typeof(FakeEntity).FullName!);

        result.Should().Be(key);
    }

    // ── GetDataSource(Type): engine extraction ──
    [Fact]
    public void GetDataSource_ByType_ReturnsEngineOfRegistryKey()
    {
        _registry
            .Setup(r => r.GetDataSourceKey(typeof(FakeEntity)))
            .Returns(new DataSourceKey(DataSource.SQLServer, "Sales"));

        var result = _sut.GetDataSource(typeof(FakeEntity));

        result.Should().Be(DataSource.SQLServer);
    }

    // ── GetDataSource(string): engine extraction ──
    [Fact]
    public void GetDataSource_ByName_ReturnsEngineOfRegistryKey()
    {
        _registry
            .Setup(r => r.GetDataSourceKey(typeof(FakeEntity).FullName!))
            .Returns(DataSourceKey.Default(DataSource.Sqlite));

        var result = _sut.GetDataSource(typeof(FakeEntity).FullName!);

        result.Should().Be(DataSource.Sqlite);
    }

    // ── Unknown entity name: registry's InvalidOperationException propagates ──
    [Fact]
    public void GetDataSourceKey_ByName_UnknownEntity_PropagatesInvalidOperationException()
    {
        _registry
            .Setup(r => r.GetDataSourceKey("NonExistent.Entity"))
            .Throws(new InvalidOperationException("No entity type configuration registers the entity."));

        FluentActions.Invoking(() => _sut.GetDataSourceKey("NonExistent.Entity"))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void GetDataSource_ByName_UnknownEntity_PropagatesInvalidOperationException()
    {
        _registry
            .Setup(r => r.GetDataSourceKey("NonExistent.Entity"))
            .Throws(new InvalidOperationException("No entity type configuration registers the entity."));

        FluentActions.Invoking(() => _sut.GetDataSource("NonExistent.Entity"))
            .Should().Throw<InvalidOperationException>();
    }

    // ── HaveIncludeSupport(DataSourceKey, DataSourceKey) ──
    [Fact]
    public void HaveIncludeSupport_SameSQLServerKey_ReturnsTrue() =>
        _sut.HaveIncludeSupport(
            DataSourceKey.Default(DataSource.SQLServer),
            DataSourceKey.Default(DataSource.SQLServer)).Should().BeTrue();

    [Fact]
    public void HaveIncludeSupport_SameSqliteKey_ReturnsTrue() =>
        _sut.HaveIncludeSupport(
            DataSourceKey.Default(DataSource.Sqlite),
            DataSourceKey.Default(DataSource.Sqlite)).Should().BeTrue();

    [Fact]
    public void HaveIncludeSupport_DifferentEngines_ReturnsFalse() =>
        _sut.HaveIncludeSupport(
            DataSourceKey.Default(DataSource.SQLServer),
            DataSourceKey.Default(DataSource.Sqlite)).Should().BeFalse();

    [Fact]
    public void HaveIncludeSupport_SameCosmosKey_ReturnsFalse() =>
        _sut.HaveIncludeSupport(
            DataSourceKey.Default(DataSource.CosmosDB),
            DataSourceKey.Default(DataSource.CosmosDB)).Should().BeFalse();

    [Fact]
    public void HaveIncludeSupport_SameEngineDifferentName_ReturnsFalse() =>
        _sut.HaveIncludeSupport(
            new DataSourceKey(DataSource.SQLServer, "Sales"),
            new DataSourceKey(DataSource.SQLServer, "Catalog")).Should().BeFalse();

    // ── HaveIncludeSupport(string, string) ──
    [Fact]
    public void HaveIncludeSupport_ByName_SamePhysicalSource_ReturnsTrue()
    {
        var key = DataSourceKey.Default(DataSource.SQLServer);
        _registry.Setup(r => r.TryGetDataSourceKey("Entity.A", out key)).Returns(true);
        _registry.Setup(r => r.TryGetDataSourceKey("Entity.B", out key)).Returns(true);

        _sut.HaveIncludeSupport("Entity.A", "Entity.B").Should().BeTrue();
    }

    [Fact]
    public void HaveIncludeSupport_ByName_DifferentEngines_ReturnsFalse()
    {
        var sqlKey = DataSourceKey.Default(DataSource.SQLServer);
        var sqliteKey = DataSourceKey.Default(DataSource.Sqlite);
        _registry.Setup(r => r.TryGetDataSourceKey("Entity.A", out sqlKey)).Returns(true);
        _registry.Setup(r => r.TryGetDataSourceKey("Entity.B", out sqliteKey)).Returns(true);

        _sut.HaveIncludeSupport("Entity.A", "Entity.B").Should().BeFalse();
    }

    [Fact]
    public void HaveIncludeSupport_ByName_FirstUnregistered_ReturnsFalse()
    {
        var key = DataSourceKey.Default(DataSource.SQLServer);
        _registry.Setup(r => r.TryGetDataSourceKey("Entity.B", out key)).Returns(true);

        _sut.HaveIncludeSupport("NonExistent.Entity", "Entity.B").Should().BeFalse();
    }

    [Fact]
    public void HaveIncludeSupport_ByName_SecondUnregistered_ReturnsFalse()
    {
        var key = DataSourceKey.Default(DataSource.SQLServer);
        _registry.Setup(r => r.TryGetDataSourceKey("Entity.A", out key)).Returns(true);

        _sut.HaveIncludeSupport("Entity.A", "NonExistent.Entity").Should().BeFalse();
    }

    // ── Test types ──
    private sealed class FakeEntity { }
}
