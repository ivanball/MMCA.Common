using AwesomeAssertions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Domain.Entities;
using Moq;

namespace MMCA.Common.Application.Tests.Services;

public sealed class EntityQueryPipelineTests
{
    private readonly Mock<IQueryableExecutor> _executorMock = new();
    private readonly EntityQueryPipeline _sut;

    public EntityQueryPipelineTests() =>
        _sut = new EntityQueryPipeline(_executorMock.Object);

    // ── Test entity ──
    private sealed class TestEntity : AuditableBaseEntity<int>
    {
        public string Name { get; init; } = string.Empty;
    }

    // ── Helpers ──
    private static NavigationMetadata EmptyNavigation() => new();

    private static EntityQueryParameters<TestEntity> DefaultParams() =>
        new()
        {
            DTOToEntityPropertyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

    // ── Basic execution ──
    [Fact]
    public async Task ExecuteAsync_NoFiltersNoPagination_ReturnsAllItems()
    {
        var entities = new List<TestEntity>
        {
            new() { Id = 1, Name = "A" },
            new() { Id = 2, Name = "B" },
        };
        var query = entities.AsQueryable();

        _executorMock
            .Setup(e => e.ToListAsync(It.IsAny<IQueryable<TestEntity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);

        var (items, totalCount) = await _sut.ExecuteAsync<TestEntity, int>(
            query,
            EmptyNavigation(),
            DefaultParams(),
            (_, _, _, _, _) => Task.CompletedTask,
            CancellationToken.None);

        items.Should().HaveCount(2);
        totalCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_WithPagination_ReturnsPagedResults()
    {
        var entities = new List<TestEntity>
        {
            new() { Id = 1, Name = "A" },
        };
        var query = entities.AsQueryable();

        _executorMock
            .Setup(e => e.CountAsync(It.IsAny<IQueryable<TestEntity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(10);
        _executorMock
            .Setup(e => e.ToListAsync(It.IsAny<IQueryable<TestEntity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);

        var parameters = new EntityQueryParameters<TestEntity>
        {
            PageNumber = 2,
            PageSize = 5,
            DTOToEntityPropertyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        var (_, totalCount) = await _sut.ExecuteAsync<TestEntity, int>(
            query,
            EmptyNavigation(),
            parameters,
            (_, _, _, _, _) => Task.CompletedTask,
            CancellationToken.None);

        totalCount.Should().Be(10);
    }

    [Fact]
    public async Task ExecuteAsync_WithSupportedIncludes_CallsInclude()
    {
        var entities = new List<TestEntity> { new() { Id = 1, Name = "A" } };
        var query = entities.AsQueryable();

        _executorMock
            .Setup(e => e.Include(It.IsAny<IQueryable<TestEntity>>(), "Name"))
            .Returns(query);
        _executorMock
            .Setup(e => e.ToListAsync(It.IsAny<IQueryable<TestEntity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);

        var navigation = new NavigationMetadata();
        // Use reflection to call internal AddSupported
        var addSupportedMethod = typeof(NavigationMetadata).GetMethod(
            "AddSupported",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        addSupportedMethod.Invoke(navigation, [new NavigationPropertyInfo("Name", NavigationType.ForeignKey, typeof(TestEntity), typeof(string))]);

        await _sut.ExecuteAsync<TestEntity, int>(
            query,
            navigation,
            DefaultParams(),
            (_, _, _, _, _) => Task.CompletedTask,
            CancellationToken.None);

        _executorMock.Verify(e => e.Include(It.IsAny<IQueryable<TestEntity>>(), "Name"), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnsupportedIncludes_CallsNavigationPopulator()
    {
        var entities = new List<TestEntity> { new() { Id = 1, Name = "A" } };
        var query = entities.AsQueryable();
        var populatorCalled = false;

        _executorMock
            .Setup(e => e.ToListAsync(It.IsAny<IQueryable<TestEntity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);

        var navigation = new NavigationMetadata();
        var addUnsupportedMethod = typeof(NavigationMetadata).GetMethod(
            "AddUnsupported",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        addUnsupportedMethod.Invoke(navigation, [new NavigationPropertyInfo("Name", NavigationType.ForeignKey, typeof(TestEntity), typeof(string))]);

        var (_, totalCount) = await _sut.ExecuteAsync<TestEntity, int>(
            query,
            navigation,
            DefaultParams(),
            (_, _, _, _, _) =>
            {
                populatorCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        populatorCalled.Should().BeTrue();
        totalCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedIncludes_WithPagination_CallsCountAsync()
    {
        var entities = new List<TestEntity> { new() { Id = 1, Name = "A" } };
        var query = entities.AsQueryable();

        _executorMock
            .Setup(e => e.CountAsync(It.IsAny<IQueryable<TestEntity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(25);
        _executorMock
            .Setup(e => e.ToListAsync(It.IsAny<IQueryable<TestEntity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);

        var navigation = new NavigationMetadata();
        var addUnsupportedMethod = typeof(NavigationMetadata).GetMethod(
            "AddUnsupported",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        addUnsupportedMethod.Invoke(navigation, [new NavigationPropertyInfo("Name", NavigationType.ForeignKey, typeof(TestEntity), typeof(string))]);

        var parameters = new EntityQueryParameters<TestEntity>
        {
            PageNumber = 1,
            PageSize = 10,
            DTOToEntityPropertyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        var (_, totalCount) = await _sut.ExecuteAsync<TestEntity, int>(
            query,
            navigation,
            parameters,
            (_, _, _, _, _) => Task.CompletedTask,
            CancellationToken.None);

        totalCount.Should().Be(25);
        _executorMock.Verify(e => e.CountAsync(It.IsAny<IQueryable<TestEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedIncludes_EmptyResult_DoesNotCallPopulator()
    {
        var query = Array.Empty<TestEntity>().AsQueryable();
        var populatorCalled = false;

        _executorMock
            .Setup(e => e.ToListAsync(It.IsAny<IQueryable<TestEntity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var navigation = new NavigationMetadata();
        var addUnsupportedMethod = typeof(NavigationMetadata).GetMethod(
            "AddUnsupported",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        addUnsupportedMethod.Invoke(navigation, [new NavigationPropertyInfo("Name", NavigationType.ForeignKey, typeof(TestEntity), typeof(string))]);

        await _sut.ExecuteAsync<TestEntity, int>(
            query,
            navigation,
            DefaultParams(),
            (_, _, _, _, _) =>
            {
                populatorCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        populatorCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WithCriteria_FiltersResults()
    {
        var entities = new List<TestEntity>
        {
            new() { Id = 1, Name = "Alpha" },
            new() { Id = 2, Name = "Beta" },
        };
        var query = entities.AsQueryable();

        _executorMock
            .Setup(e => e.ToListAsync(It.IsAny<IQueryable<TestEntity>>(), It.IsAny<CancellationToken>()))
            .Returns<IQueryable<TestEntity>, CancellationToken>((q, _) => Task.FromResult(q.ToList()));

        var parameters = new EntityQueryParameters<TestEntity>
        {
            Criteria = e => e.Name == "Alpha",
            DTOToEntityPropertyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        var (items, _) = await _sut.ExecuteAsync<TestEntity, int>(
            query,
            EmptyNavigation(),
            parameters,
            (_, _, _, _, _) => Task.CompletedTask,
            CancellationToken.None);

        items.Should().HaveCount(1);
        items.First().Name.Should().Be("Alpha");
    }
}
