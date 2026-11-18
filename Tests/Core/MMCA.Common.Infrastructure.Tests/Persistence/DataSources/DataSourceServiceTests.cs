using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DataSources;

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
    // A null name means the engine's default key.
    [Theory]
    [InlineData(DataSource.SQLServer, null, DataSource.SQLServer, null, true)]
    [InlineData(DataSource.Sqlite, null, DataSource.Sqlite, null, true)]
    [InlineData(DataSource.SQLServer, null, DataSource.Sqlite, null, false)]
    [InlineData(DataSource.CosmosDB, null, DataSource.CosmosDB, null, false)]
    [InlineData(DataSource.SQLServer, "Sales", DataSource.SQLServer, "Catalog", false)]
    public void HaveIncludeSupport_ByKey_IsTrueOnlyForTheSameRelationalSource(
        DataSource firstEngine,
        string? firstName,
        DataSource secondEngine,
        string? secondName,
        bool expected) =>
        _sut.HaveIncludeSupport(Key(firstEngine, firstName), Key(secondEngine, secondName))
            .Should().Be(expected);

    private static DataSourceKey Key(DataSource engine, string? name) =>
        name is null ? DataSourceKey.Default(engine) : new DataSourceKey(engine, name);

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

    // ── GetDataSource(Type): unregistered type propagates the registry exception ──
    [Fact]
    public void GetDataSource_ByType_WhenUnregistered_PropagatesInvalidOperationException()
    {
        _registry
            .Setup(r => r.GetDataSourceKey(typeof(UnregisteredEntity)))
            .Throws(new InvalidOperationException("No entity type configuration registers the entity."));

        FluentActions.Invoking(() => _sut.GetDataSource(typeof(UnregisteredEntity)))
            .Should().Throw<InvalidOperationException>();
    }

    // ── GetDataSource(Type): Cosmos engine extraction ──
    [Fact]
    public void GetDataSource_ByType_CosmosEntity_ReturnsCosmosDB()
    {
        _registry
            .Setup(r => r.GetDataSourceKey(typeof(FakeEntity)))
            .Returns(DataSourceKey.Default(DataSource.CosmosDB));

        _sut.GetDataSource(typeof(FakeEntity)).Should().Be(DataSource.CosmosDB);
    }

    // ── HaveIncludeSupport(string, string): both Cosmos, same key → false ──
    [Fact]
    public void HaveIncludeSupport_ByName_BothCosmosSameKey_ReturnsFalse()
    {
        var cosmosKey = DataSourceKey.Default(DataSource.CosmosDB);
        _registry.Setup(r => r.TryGetDataSourceKey("Cosmos.Entity.A", out cosmosKey)).Returns(true);
        _registry.Setup(r => r.TryGetDataSourceKey("Cosmos.Entity.B", out cosmosKey)).Returns(true);

        _sut.HaveIncludeSupport("Cosmos.Entity.A", "Cosmos.Entity.B").Should().BeFalse();
    }

    // ── HaveIncludeSupport(string, string): same engine, different database → false ──
    [Fact]
    public void HaveIncludeSupport_ByName_SameEngineDifferentName_ReturnsFalse()
    {
        var salesKey = new DataSourceKey(DataSource.SQLServer, "Sales");
        var catalogKey = new DataSourceKey(DataSource.SQLServer, "Catalog");
        _registry.Setup(r => r.TryGetDataSourceKey("Sales.Entity", out salesKey)).Returns(true);
        _registry.Setup(r => r.TryGetDataSourceKey("Catalog.Entity", out catalogKey)).Returns(true);

        _sut.HaveIncludeSupport("Sales.Entity", "Catalog.Entity").Should().BeFalse();
    }

    // ── HaveIncludeSupport(string, string): same non-default Sqlite source → true ──
    [Fact]
    public void HaveIncludeSupport_ByName_SameNamedSqliteSource_ReturnsTrue()
    {
        var key = new DataSourceKey(DataSource.Sqlite, "Engagement");
        _registry.Setup(r => r.TryGetDataSourceKey("Sqlite.Entity.A", out key)).Returns(true);
        _registry.Setup(r => r.TryGetDataSourceKey("Sqlite.Entity.B", out key)).Returns(true);

        _sut.HaveIncludeSupport("Sqlite.Entity.A", "Sqlite.Entity.B").Should().BeTrue();
    }

    // ── HaveIncludeSupport(string, string): neither registered → false ──
    [Fact]
    public void HaveIncludeSupport_ByName_NeitherRegistered_ReturnsFalse() =>
        _sut.HaveIncludeSupport("NonExistent.Entity1", "NonExistent.Entity2").Should().BeFalse();

    // ── Test types ──
    private sealed class FakeEntity;

    private sealed class UnregisteredEntity;
}
