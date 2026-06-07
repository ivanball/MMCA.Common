using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Services;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Services;

/// <summary>
/// Additional <see cref="DataSourceService"/> facade tests complementing
/// <see cref="DataSourceServiceTests"/>: edge cases for include-support
/// classification by entity name and engine extraction by CLR type.
/// </summary>
public sealed class DataSourceServiceAdditionalTests
{
    private readonly Mock<IEntityDataSourceRegistry> _registry = new();
    private readonly DataSourceService _sut;

    public DataSourceServiceAdditionalTests() => _sut = new DataSourceService(_registry.Object);

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
    private sealed class FakeEntity { }

    private sealed class UnregisteredEntity { }
}
