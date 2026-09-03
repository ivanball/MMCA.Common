using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.DTOs;
using Moq;

namespace MMCA.Common.Application.Tests.Services.Query;

public sealed class EntityQueryServiceTests
{
    public sealed class FakeEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class FakeEntityDTO : IBaseDTO<int>
    {
        public required int Id { get; init; }
        public string Name { get; set; } = string.Empty;
    }

    private static Mock<IUnitOfWork> CreateMockUnitOfWork()
    {
        var mock = new Mock<IUnitOfWork>();
        mock.Setup(x => x.GetReadRepository<FakeEntity, int>())
            .Returns(new Mock<IReadRepository<FakeEntity, int>>().Object);
        return mock;
    }

    // ── Constructor null guards ──
    [Fact]
    public void Constructor_WithNullUnitOfWork_ThrowsArgumentNullException()
    {
        var act = () => new EntityQueryService<FakeEntity, FakeEntityDTO, int>(
            null!,
            Mock.Of<INavigationMetadataProvider>(),
            Mock.Of<IEntityQueryPipeline>(),
            Mock.Of<IEntityDTOMapper<FakeEntity, FakeEntityDTO, int>>(),
            Mock.Of<INavigationPopulator<FakeEntity>>());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithNullNavigationMetadataProvider_ThrowsArgumentNullException()
    {
        var act = () => new EntityQueryService<FakeEntity, FakeEntityDTO, int>(
            CreateMockUnitOfWork().Object,
            null!,
            Mock.Of<IEntityQueryPipeline>(),
            Mock.Of<IEntityDTOMapper<FakeEntity, FakeEntityDTO, int>>(),
            Mock.Of<INavigationPopulator<FakeEntity>>());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithNullQueryPipeline_ThrowsArgumentNullException()
    {
        var act = () => new EntityQueryService<FakeEntity, FakeEntityDTO, int>(
            CreateMockUnitOfWork().Object,
            Mock.Of<INavigationMetadataProvider>(),
            null!,
            Mock.Of<IEntityDTOMapper<FakeEntity, FakeEntityDTO, int>>(),
            Mock.Of<INavigationPopulator<FakeEntity>>());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithNullDTOMapper_ThrowsArgumentNullException()
    {
        var act = () => new EntityQueryService<FakeEntity, FakeEntityDTO, int>(
            CreateMockUnitOfWork().Object,
            Mock.Of<INavigationMetadataProvider>(),
            Mock.Of<IEntityQueryPipeline>(),
            null!,
            Mock.Of<INavigationPopulator<FakeEntity>>());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithNullNavigationPopulator_ThrowsArgumentNullException()
    {
        var act = () => new EntityQueryService<FakeEntity, FakeEntityDTO, int>(
            CreateMockUnitOfWork().Object,
            Mock.Of<INavigationMetadataProvider>(),
            Mock.Of<IEntityQueryPipeline>(),
            Mock.Of<IEntityDTOMapper<FakeEntity, FakeEntityDTO, int>>(),
            null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithAllDependencies_CreatesSut()
    {
        var unitOfWork = CreateMockUnitOfWork();

        var sut = new EntityQueryService<FakeEntity, FakeEntityDTO, int>(
            unitOfWork.Object,
            Mock.Of<INavigationMetadataProvider>(),
            Mock.Of<IEntityQueryPipeline>(),
            Mock.Of<IEntityDTOMapper<FakeEntity, FakeEntityDTO, int>>(),
            Mock.Of<INavigationPopulator<FakeEntity>>());

        sut.Should().NotBeNull();
        sut.DTOMapper.Should().NotBeNull();
        sut.NavigationPopulator.Should().NotBeNull();
    }

    // ── ExistsAsync delegates to repository ──
    [Fact]
    public async Task ExistsAsync_DelegatesToRepository()
    {
        var mockReadRepo = new Mock<IReadRepository<FakeEntity, int>>();
        mockReadRepo.Setup(x => x.ExistsAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<FakeEntity, bool>>>(),
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.GetReadRepository<FakeEntity, int>()).Returns(mockReadRepo.Object);

        var sut = new EntityQueryService<FakeEntity, FakeEntityDTO, int>(
            unitOfWork.Object,
            Mock.Of<INavigationMetadataProvider>(),
            Mock.Of<IEntityQueryPipeline>(),
            Mock.Of<IEntityDTOMapper<FakeEntity, FakeEntityDTO, int>>(),
            Mock.Of<INavigationPopulator<FakeEntity>>());

        var result = await sut.ExistsAsync(e => e.Id == 1);

        result.Should().BeTrue();
    }

    // ── By-id fast path ──
    [Fact]
    public async Task GetEntityByIdAsync_PlainKeyLookup_UsesKeyedRepositoryFastPath()
    {
        var entity = new FakeEntity { Id = 5, Name = "Five" };
        var (sut, repo) = CreateSutWithReadRepo();
        repo.Setup(r => r.GetByIdAsync(5, It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        // The pipeline is left unmocked: a success here can ONLY come from the keyed fast path,
        // since the pipeline mock returns nothing.
        var result = await sut.GetEntityByIdAsync("5");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(entity);
        repo.Verify(r => r.GetByIdAsync(5, It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetEntityByIdAsync_PlainKeyLookup_WhenMissing_ReturnsNotFound()
    {
        var (sut, repo) = CreateSutWithReadRepo();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FakeEntity?)null);

        var result = await sut.GetEntityByIdAsync("5");

        result.IsFailure.Should().BeTrue();
    }

    private static (EntityQueryService<FakeEntity, FakeEntityDTO, int> Sut, Mock<IReadRepository<FakeEntity, int>> Repo) CreateSutWithReadRepo()
    {
        var repo = new Mock<IReadRepository<FakeEntity, int>>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.GetReadRepository<FakeEntity, int>()).Returns(repo.Object);

        var sut = new EntityQueryService<FakeEntity, FakeEntityDTO, int>(
            unitOfWork.Object,
            Mock.Of<INavigationMetadataProvider>(),
            Mock.Of<IEntityQueryPipeline>(),
            Mock.Of<IEntityDTOMapper<FakeEntity, FakeEntityDTO, int>>(),
            Mock.Of<INavigationPopulator<FakeEntity>>());

        return (sut, repo);
    }

    // ── DTOToEntityPropertyMap defaults to empty ──
    [Fact]
    public void DTOToEntityPropertyMap_DefaultsToEmptyDictionary()
    {
        var unitOfWork = CreateMockUnitOfWork();

        var sut = new TestableEntityQueryService(
            unitOfWork.Object,
            Mock.Of<INavigationMetadataProvider>(),
            Mock.Of<IEntityQueryPipeline>(),
            Mock.Of<IEntityDTOMapper<FakeEntity, FakeEntityDTO, int>>(),
            Mock.Of<INavigationPopulator<FakeEntity>>());

        sut.GetPropertyMap().Should().BeEmpty();
    }

    private sealed class TestableEntityQueryService(
        IUnitOfWork unitOfWork,
        INavigationMetadataProvider navigationMetadataProvider,
        IEntityQueryPipeline queryPipeline,
        IEntityDTOMapper<FakeEntity, FakeEntityDTO, int> dtoMapper,
        INavigationPopulator<FakeEntity> navigationPopulator)
        : EntityQueryService<FakeEntity, FakeEntityDTO, int>(unitOfWork, navigationMetadataProvider, queryPipeline, dtoMapper, navigationPopulator)
    {
        public IReadOnlyDictionary<string, string> GetPropertyMap() => DTOToEntityPropertyMap;
    }

    // ── Harness: the REAL query pipeline over an in-memory queryable ──
    // Sorting, paging and the pagination metadata are only meaningful together, so these tests
    // drive the real EntityQueryPipeline and only fake the EF-facing executor.
    private sealed class InMemoryQueryableExecutor : IQueryableExecutor
    {
        public IQueryable<T> Include<T>(IQueryable<T> query, string navigationPropertyPath)
            where T : class
            => query;

        public IQueryable<T> AsSplitQuery<T>(IQueryable<T> query)
            where T : class
            => query;

        public Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
            => Task.FromResult(query.ToList());

        public Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
            => Task.FromResult(query.Count());
    }

    private sealed class FakeEntityDTOMapper : IEntityDTOMapper<FakeEntity, FakeEntityDTO, int>
    {
        public FakeEntityDTO MapToDTO(FakeEntity entity) => new() { Id = entity.Id, Name = entity.Name };
    }

    /// <summary>
    /// A subclass that maps a DTO-facing name onto an entity property, exactly as a module's own
    /// query service does (for example "CategoryName" -> "Category.Name").
    /// </summary>
    private sealed class MappedEntityQueryService(
        IUnitOfWork unitOfWork,
        INavigationMetadataProvider navigationMetadataProvider,
        IEntityQueryPipeline queryPipeline,
        IEntityDTOMapper<FakeEntity, FakeEntityDTO, int> dtoMapper,
        INavigationPopulator<FakeEntity> navigationPopulator)
        : EntityQueryService<FakeEntity, FakeEntityDTO, int>(unitOfWork, navigationMetadataProvider, queryPipeline, dtoMapper, navigationPopulator)
    {
        protected override IReadOnlyDictionary<string, string> DTOToEntityPropertyMap { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["DisplayName"] = "Name" };
    }

    private static MappedEntityQueryService CreateSutOver(
        List<FakeEntity> data,
        IEntityQueryPipeline? pipeline = null)
    {
        var repo = new Mock<IReadRepository<FakeEntity, int>>();
        repo.Setup(r => r.Table).Returns(data.AsQueryable());
        repo.Setup(r => r.TableNoTracking).Returns(data.AsQueryable());

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.GetReadRepository<FakeEntity, int>()).Returns(repo.Object);

        var navigationMetadataProvider = new Mock<INavigationMetadataProvider>();
        navigationMetadataProvider
            .Setup(p => p.BuildIncludes<FakeEntity>(It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns(new NavigationMetadata());

        return new MappedEntityQueryService(
            unitOfWork.Object,
            navigationMetadataProvider.Object,
            pipeline ?? new EntityQueryPipeline(new InMemoryQueryableExecutor()),
            new FakeEntityDTOMapper(),
            Mock.Of<INavigationPopulator<FakeEntity>>());
    }

    private static List<FakeEntity> Rows(int count)
        => [.. Enumerable.Range(1, count).Select(i => new FakeEntity { Id = i, Name = "Row" })];

    // ── Sort-column validation honors the DTO-to-entity map ──
    [Fact]
    public async Task GetAllAsync_MappedSortColumn_PassesValidationAndSorts()
    {
        var sut = CreateSutOver(
        [
            new FakeEntity { Id = 1, Name = "C" },
            new FakeEntity { Id = 2, Name = "A" },
            new FakeEntity { Id = 3, Name = "B" },
        ]);

        // "DisplayName" is not a property of FakeEntity: only the map makes it sortable, and the
        // sort would have worked all along. Validating it without the map returned a 400 instead.
        var result = await sut.GetAllAsync(sortColumn: "DisplayName", sortDirection: "asc");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Cast<FakeEntityDTO>().Select(d => d.Name).Should().Equal("A", "B", "C");
    }

    [Fact]
    public async Task GetAllAsync_MappedSortColumn_Descending_Sorts()
    {
        var sut = CreateSutOver(
        [
            new FakeEntity { Id = 1, Name = "A" },
            new FakeEntity { Id = 2, Name = "B" },
        ]);

        var result = await sut.GetAllAsync(sortColumn: "DisplayName", sortDirection: "desc");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Cast<FakeEntityDTO>().Select(d => d.Name).Should().Equal("B", "A");
    }

    [Fact]
    public async Task GetAllAsync_UnmappedBogusSortColumn_StillReturnsValidationFailure()
    {
        var sut = CreateSutOver([new FakeEntity { Id = 1, Name = "A" }]);

        var result = await sut.GetAllAsync(sortColumn: "TotallyBogus");

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "Error.InvalidEntityField");
    }

    [Fact]
    public async Task GetAllAsync_UnmappedRealSortColumn_StillPassesValidation()
    {
        var sut = CreateSutOver(
        [
            new FakeEntity { Id = 2, Name = "B" },
            new FakeEntity { Id = 1, Name = "A" },
        ]);

        var result = await sut.GetAllAsync(sortColumn: "Name", sortDirection: "asc");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Cast<FakeEntityDTO>().Select(d => d.Id).Should().Equal(1, 2);
    }

    // ── Pagination metadata: floors, ceiling, and coherent derived values ──
    [Theory]
    [InlineData(0, 0, 1, 1, 1, 25)]
    [InlineData(-3, -5, 1, 1, 1, 25)]
    [InlineData(1, 0, 1, 1, 1, 25)]
    [InlineData(0, 10, 1, 10, 10, 3)]
    public async Task GetAllAsync_NonPositivePaging_ReportsThePageThePipelineActuallyApplied(
        int pageNumber,
        int pageSize,
        int expectedCurrentPage,
        int expectedPageSize,
        int expectedItemCount,
        int expectedTotalPageCount)
    {
        var sut = CreateSutOver(Rows(25));

        var result = await sut.GetAllAsync(pageNumber: pageNumber, pageSize: pageSize);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().HaveCount(expectedItemCount);

        var metadata = result.Value.PaginationMetadata;
        metadata.TotalItemCount.Should().Be(25);
        metadata.PageSize.Should().Be(expectedPageSize);
        metadata.CurrentPage.Should().Be(expectedCurrentPage);
        metadata.TotalPageCount.Should().Be(expectedTotalPageCount);
        metadata.FirstRowOnPage.Should().Be(1);
        metadata.LastRowOnPage.Should().Be(expectedPageSize);
    }

    [Fact]
    public async Task GetAllAsync_PageSizeAboveTheCeiling_ReportsTheCeiling()
    {
        var sut = CreateSutOver(Rows(25));

        var result = await sut.GetAllAsync(pageNumber: 1, pageSize: 5000);

        result.Value!.PaginationMetadata.PageSize.Should().Be(EntityQueryPipeline.MaxUnboundedResultLimit);
    }

    [Fact]
    public async Task GetAllAsync_PageBeyondTheReachableOffset_StillReportsTheClampedPageSize()
    {
        var sut = CreateSutOver(Rows(25));

        // PagingMath returns its (0, 0) offset-overflow sentinel here, so the page is genuinely
        // empty; the metadata must still report the page size the request asked for, not 0.
        var result = await sut.GetAllAsync(pageNumber: int.MaxValue, pageSize: 10);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().BeEmpty();
        result.Value.PaginationMetadata.PageSize.Should().Be(10);
        result.Value.PaginationMetadata.TotalItemCount.Should().Be(25);
    }

    [Fact]
    public async Task GetAllAsync_Unpaginated_ReportsTheMaterializedRowCountAsPageSize()
    {
        var sut = CreateSutOver(Rows(7));

        var result = await sut.GetAllAsync(sortColumn: null);

        var metadata = result.Value!.PaginationMetadata;
        metadata.TotalItemCount.Should().Be(7);
        metadata.PageSize.Should().Be(7);
        metadata.CurrentPage.Should().Be(1);
        metadata.TotalPageCount.Should().Be(1);
        metadata.FirstRowOnPage.Should().Be(1);
        metadata.LastRowOnPage.Should().Be(7);
    }

    [Fact]
    public async Task GetAllAsync_UnpaginatedEmptyResult_ProducesCoherentMetadata()
    {
        var sut = CreateSutOver([]);

        var result = await sut.GetAllAsync(sortColumn: null);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().BeEmpty();

        var metadata = result.Value.PaginationMetadata;
        metadata.TotalItemCount.Should().Be(0);
        metadata.PageSize.Should().Be(0);
        metadata.CurrentPage.Should().Be(1);
        metadata.TotalPageCount.Should().Be(0);
        metadata.FirstRowOnPage.Should().Be(0);
        metadata.LastRowOnPage.Should().Be(0);
    }

    [Fact]
    public async Task GetAllAsync_NegativeTotalFromThePipeline_IsFlooredInsteadOfThrowing()
    {
        var pipeline = new Mock<IEntityQueryPipeline>();
        pipeline
            .Setup(p => p.ExecuteAsync<FakeEntity, int>(
                It.IsAny<IQueryable<FakeEntity>>(),
                It.IsAny<NavigationMetadata>(),
                It.IsAny<EntityQueryParameters<FakeEntity>>(),
                It.IsAny<Func<IReadOnlyCollection<FakeEntity>, NavigationMetadata, bool, bool, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<(IReadOnlyCollection<FakeEntity> Items, int TotalCount)>(
                (Array.Empty<FakeEntity>(), -5)));

        var sut = CreateSutOver([], pipeline.Object);

        // The validating constructor rejects negatives, so an impossible count must be floored
        // rather than allowed to turn a successful read into an exception.
        var result = await sut.GetAllAsync(sortColumn: null);

        result.IsSuccess.Should().BeTrue();

        var metadata = result.Value!.PaginationMetadata;
        metadata.TotalItemCount.Should().Be(0);
        metadata.PageSize.Should().Be(0);
        metadata.CurrentPage.Should().Be(1);
    }

    // ── GetAllForLookupAsync forwards its predicate ──
    // Regression guard: the `where` argument used to be accepted and then dropped, so every
    // caller got the unfiltered lookup list and had to route around the service.
    private static Mock<IReadRepository<FakeEntity, int>> CreateLookupRepositoryOver(params FakeEntity[] rows)
    {
        var repository = new Mock<IReadRepository<FakeEntity, int>>();
        repository
            .Setup(x => x.GetAllForLookupAsync(
                It.IsAny<string>(),
                It.IsAny<Expression<Func<FakeEntity, bool>>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                string nameProperty,
                Expression<Func<FakeEntity, bool>>? where,
                bool asTracking,
                CancellationToken cancellationToken) =>
            {
                // Mirrors EFReadRepository.GetAllForLookupAsync: the predicate narrows the
                // query before the id/name projection is applied.
                IEnumerable<FakeEntity> matched = where is null ? rows : rows.Where(where.Compile());

                IReadOnlyCollection<BaseLookup<int>> lookups =
                [
                    .. matched
                        .Select(e => new BaseLookup<int> { Id = e.Id, Name = e.Name })
                        .OrderBy(l => l.Name, StringComparer.Ordinal)
                ];

                return lookups;
            });

        return repository;
    }

    private static EntityQueryService<FakeEntity, FakeEntityDTO, int> CreateLookupSut(
        Mock<IReadRepository<FakeEntity, int>> repository)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.GetReadRepository<FakeEntity, int>()).Returns(repository.Object);

        return new EntityQueryService<FakeEntity, FakeEntityDTO, int>(
            unitOfWork.Object,
            Mock.Of<INavigationMetadataProvider>(),
            Mock.Of<IEntityQueryPipeline>(),
            Mock.Of<IEntityDTOMapper<FakeEntity, FakeEntityDTO, int>>(),
            Mock.Of<INavigationPopulator<FakeEntity>>());
    }

    [Fact]
    public async Task GetAllForLookupAsync_WithPredicate_FiltersTheLookupRows()
    {
        var repository = CreateLookupRepositoryOver(
            new FakeEntity { Id = 1, Name = "Alpha" },
            new FakeEntity { Id = 2, Name = "Beta" },
            new FakeEntity { Id = 3, Name = "Gamma" });

        var sut = CreateLookupSut(repository);

        Expression<Func<FakeEntity, bool>> where = e => e.Id != 2;

        var result = await sut.GetAllForLookupAsync(nameof(FakeEntity.Name), where);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(2);
        result.Value!.Should().Contain(l => l.Id == 1);
        result.Value!.Should().Contain(l => l.Id == 3);
        result.Value!.Should().NotContain(l => l.Name == "Beta");
    }

    [Fact]
    public async Task GetAllForLookupAsync_WithPredicate_PassesTheSameExpressionToTheRepository()
    {
        var repository = CreateLookupRepositoryOver(new FakeEntity { Id = 1, Name = "Alpha" });
        var sut = CreateLookupSut(repository);

        Expression<Func<FakeEntity, bool>> where = e => e.Id == 1;

        await sut.GetAllForLookupAsync(nameof(FakeEntity.Name), where, asTracking: true);

        repository.Verify(
            x => x.GetAllForLookupAsync(
                nameof(FakeEntity.Name),
                where,
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetAllForLookupAsync_WithoutPredicate_ReturnsEveryRow()
    {
        var repository = CreateLookupRepositoryOver(
            new FakeEntity { Id = 1, Name = "Alpha" },
            new FakeEntity { Id = 2, Name = "Beta" });

        var sut = CreateLookupSut(repository);

        var result = await sut.GetAllForLookupAsync(nameof(FakeEntity.Name));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(2);

        repository.Verify(
            x => x.GetAllForLookupAsync(
                nameof(FakeEntity.Name),
                null,
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetAllForLookupAsync_WithUnknownNameProperty_FailsBeforeTouchingTheRepository()
    {
        var repository = CreateLookupRepositoryOver(new FakeEntity { Id = 1, Name = "Alpha" });
        var sut = CreateLookupSut(repository);

        var result = await sut.GetAllForLookupAsync("NoSuchProperty", e => e.Id == 1);

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Source.Should().Be(nameof(EntityQueryService<,,>.GetAllForLookupAsync));
        result.Errors[0].Target.Should().Be(nameof(FakeEntity));

        repository.Verify(
            x => x.GetAllForLookupAsync(
                It.IsAny<string>(),
                It.IsAny<Expression<Func<FakeEntity, bool>>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
