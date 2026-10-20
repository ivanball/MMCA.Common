using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.Interfaces.Navigation;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Testing.Support;
using Moq;

namespace MMCA.Common.Application.Tests.Services.Query;

/// <summary>
/// Covers the opt-in projection path of <see cref="EntityQueryService{TEntity, TEntityDTO, TIdentifierType}"/>:
/// when a projector is registered the query selects DTO columns and the mapper is never called; when
/// the read cannot be projected (cross-source includes, tracking) the service falls back to the
/// unchanged materialize-then-map path.
/// </summary>
public sealed class EntityQueryServiceProjectionTests
{
    public sealed class ProjectedEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;

        public int Rank { get; set; }
    }

    public sealed class ProjectedEntityDTO : IBaseDTO<int>
    {
        public required int Id { get; init; }

        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Declared on the DTO because the sort/filter tests below name it. Since SEC-ADC-09 the
        /// query service resolves client-supplied sort columns and filter keys against the RESPONSE
        /// CONTRACT, so a field only the entity carries is refused with a 400 rather than silently
        /// ordering a page by a column the caller cannot see.
        /// </summary>
        public int Rank { get; set; }
    }

    /// <summary>A mapper that records every call, so a test can prove it was bypassed.</summary>
    private sealed class SpyMapper : IEntityDTOMapper<ProjectedEntity, ProjectedEntityDTO, int>
    {
        public int MapToDTOCallCount { get; private set; }

        public int MapToDTOsCallCount { get; private set; }

        public ProjectedEntityDTO MapToDTO(ProjectedEntity entity)
        {
            MapToDTOCallCount++;
            return new ProjectedEntityDTO { Id = entity.Id, Name = "mapped:" + entity.Name };
        }

        public IReadOnlyCollection<ProjectedEntityDTO> MapToDTOs(IReadOnlyCollection<ProjectedEntity> entityCollection)
        {
            MapToDTOsCallCount++;
            return [.. entityCollection.Select(MapToDTO)];
        }
    }

    /// <summary>A projector whose values are deliberately distinguishable from the mapper's.</summary>
    private sealed class TestProjector : IEntityDTOProjector<ProjectedEntity, ProjectedEntityDTO, int>
    {
        public IQueryable<ProjectedEntityDTO> ProjectTo(IQueryable<ProjectedEntity> source) =>
            source.Select(e => new ProjectedEntityDTO { Id = e.Id, Name = "projected:" + e.Name });
    }

    private static readonly List<ProjectedEntity> Rows =
    [
        new() { Id = 2, Name = "b", Rank = 2 },
        new() { Id = 1, Name = "a", Rank = 1 },
        new() { Id = 3, Name = "c", Rank = 3 },
    ];

    private readonly SpyMapper _mapper = new();
    private readonly Mock<INavigationMetadataProvider> _navigationMetadataProvider = new();
    private readonly NavigationMetadata _navigationMetadata = new();

    private EntityQueryService<ProjectedEntity, ProjectedEntityDTO, int> CreateSut(bool withProjector)
    {
        var repository = new Mock<IReadRepository<ProjectedEntity, int>>();
        repository.SetupGet(r => r.Table).Returns(Rows.AsQueryable());
        repository.SetupGet(r => r.TableNoTracking).Returns(Rows.AsQueryable());

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.GetReadRepository<ProjectedEntity, int>()).Returns(repository.Object);

        _navigationMetadataProvider
            .Setup(p => p.BuildIncludes<ProjectedEntity>(It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns(_navigationMetadata);

        var pipeline = new EntityQueryPipeline(new InMemoryQueryableExecutor());
        var populator = new Mock<INavigationPopulator<ProjectedEntity>>();

        return withProjector
            ? new EntityQueryService<ProjectedEntity, ProjectedEntityDTO, int>(
                unitOfWork.Object, _navigationMetadataProvider.Object, pipeline, _mapper, populator.Object, new TestProjector())
            : new EntityQueryService<ProjectedEntity, ProjectedEntityDTO, int>(
                unitOfWork.Object, _navigationMetadataProvider.Object, pipeline, _mapper, populator.Object);
    }

    private void AddCrossSourceInclude() =>
        _navigationMetadata.AddUnsupported(
            new NavigationPropertyInfo("Elsewhere", NavigationType.ForeignKey, typeof(ProjectedEntity), typeof(ProjectedEntity)));

    // ── Projection path ──
    [Fact]
    public async Task GetAllAsync_WithAProjector_BypassesTheMapper()
    {
        var sut = CreateSut(withProjector: true);

        var result = await sut.GetAllAsync(filters: null);

        result.IsSuccess.Should().BeTrue();
        _mapper.MapToDTOsCallCount.Should().Be(0, "the projection path never materializes an entity to map");
        _mapper.MapToDTOCallCount.Should().Be(0);
        result.Value!.Items.Cast<ProjectedEntityDTO>().Select(d => d.Name)
            .Should().BeEquivalentTo("projected:a", "projected:b", "projected:c");
    }

    [Fact]
    public async Task GetAllAsync_WithoutAProjector_UsesTheMapper()
    {
        var sut = CreateSut(withProjector: false);

        var result = await sut.GetAllAsync(filters: null);

        result.IsSuccess.Should().BeTrue();
        _mapper.MapToDTOsCallCount.Should().Be(1);
        result.Value!.Items.Cast<ProjectedEntityDTO>().Select(d => d.Name)
            .Should().BeEquivalentTo("mapped:a", "mapped:b", "mapped:c");
    }

    [Fact]
    public async Task GetAllAsync_WithAProjector_StillPagesAndCounts()
    {
        var sut = CreateSut(withProjector: true);

        var result = await sut.GetAllAsync(pageNumber: 1, pageSize: 2);

        result.Value!.Items.Should().HaveCount(2);
        result.Value.PaginationMetadata.TotalItemCount.Should().Be(3);
        result.Value.Items.Cast<ProjectedEntityDTO>().Select(d => d.Id)
            .Should().Equal([1, 2], "a paged projection is still ordered by the key tie-break");
    }

    [Fact]
    public async Task GetAllAsync_WithAProjector_StillSortsAndFilters()
    {
        var sut = CreateSut(withProjector: true);

        var result = await sut.GetAllAsync(
            filters: new Dictionary<string, (string Operator, string Value)>(StringComparer.OrdinalIgnoreCase)
            {
                ["Rank"] = ("GREATER THAN", "1"),
            },
            sortColumn: "Rank",
            sortDirection: "desc",
            pageNumber: 1,
            pageSize: 10);

        result.Value!.Items.Cast<ProjectedEntityDTO>().Select(d => d.Id).Should().Equal(3, 2);
    }

    /// <summary>
    /// SEC-ADC-09: the counterpart of the test above. A field the entity has and the DTO does not is
    /// refused, so an anonymous caller cannot order a public page by a redacted or audit-only column
    /// and read its total order.
    /// </summary>
    [Fact]
    public async Task GetAllAsync_RefusesASortColumnTheResponseContractDoesNotDeclare()
    {
        var sut = CreateSut(withProjector: true);

        var result = await sut.GetAllAsync(filters: null, sortColumn: "CreatedBy");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task GetAllAsync_WithAProjectorAndFields_StillShapesTheProjectedDTOs()
    {
        var sut = CreateSut(withProjector: true);

        var result = await sut.GetAllAsync(filters: null, fields: "Id");

        _mapper.MapToDTOsCallCount.Should().Be(0);
        result.Value!.Items.Should().AllBeOfType<System.Dynamic.ExpandoObject>(
            "shaping reflects over whatever object the pipeline produced, mapped or projected");
        result.Value.Items.Cast<IDictionary<string, object?>>()
            .Should().AllSatisfy(shaped => shaped.Keys.Should().Equal("id"));
    }

    // ── Fallback conditions ──
    [Fact]
    public async Task GetAllAsync_WithCrossSourceIncludes_FallsBackToTheMapper()
    {
        AddCrossSourceInclude();
        var sut = CreateSut(withProjector: true);

        var result = await sut.GetAllAsync(includeFKs: true, filters: null);

        _mapper.MapToDTOsCallCount.Should().Be(
            1,
            "a cross-source navigation is batch-loaded after materialization, which a projection has no rows for");
        result.Value!.Items.Cast<ProjectedEntityDTO>().Select(d => d.Name)
            .Should().AllSatisfy(name => name.Should().StartWith("mapped:"));
    }

    [Fact]
    public async Task GetAllAsync_WithTracking_FallsBackToTheMapper()
    {
        var sut = CreateSut(withProjector: true);

        var result = await sut.GetAllAsync(filters: null, asTracking: true);

        _mapper.MapToDTOsCallCount.Should().Be(1, "a projection produces DTOs, which the change tracker has no use for");
        result.IsSuccess.Should().BeTrue();
    }

    // ── Widened specification parameter ──
    [Fact]
    public async Task GetAllAsync_AcceptsAnInlineSpecification()
    {
        var sut = CreateSut(withProjector: true);

        var result = await sut.GetAllAsync(
            specification: new InlineSpecification<ProjectedEntity, int>(e => e.Rank >= 2),
            filters: null);

        result.Value!.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAllAsync_AcceptsAComposedSpecification()
    {
        var sut = CreateSut(withProjector: true);

        var specification = new InlineSpecification<ProjectedEntity, int>(e => e.Rank >= 2)
            .And(new InlineSpecification<ProjectedEntity, int>(e => e.Name == "c"));

        var result = await sut.GetAllAsync(specification: specification, filters: null);

        result.Value!.Items.Cast<ProjectedEntityDTO>().Should().ContainSingle().Which.Id.Should().Be(3);
    }

    [Fact]
    public void Constructor_WithANullProjector_Throws()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.GetReadRepository<ProjectedEntity, int>())
            .Returns(new Mock<IReadRepository<ProjectedEntity, int>>().Object);

        var act = () => new EntityQueryService<ProjectedEntity, ProjectedEntityDTO, int>(
            unitOfWork.Object,
            Mock.Of<INavigationMetadataProvider>(),
            Mock.Of<IEntityQueryPipeline>(),
            _mapper,
            Mock.Of<INavigationPopulator<ProjectedEntity>>(),
            null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── fields= (M227, bug hunt 2026-10-19) ──
    // fields= names DTO properties, because shaping runs over the DTO. Validation has to agree with
    // shaping: a DTO-only name is shaped, an entity-only name has no DTO accessor and is refused
    // rather than returning empty objects. Only the non-projected path, which narrows the entity with
    // a MemberInit, still needs a writable entity property.
    public sealed class FieldsEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>Entity-only column: the DTO deliberately does not carry it.</summary>
        public string InternalNote { get; set; } = string.Empty;
    }

    public sealed class FieldsEntityDTO : IBaseDTO<int>
    {
        public required int Id { get; init; }

        public string Name { get; set; } = string.Empty;

        /// <summary>DTO-only member: computed by the mapper or projector, absent from the entity.</summary>
        public string DisplayName { get; set; } = string.Empty;
    }

    private sealed class FieldsMapper : IEntityDTOMapper<FieldsEntity, FieldsEntityDTO, int>
    {
        public FieldsEntityDTO MapToDTO(FieldsEntity entity) =>
            new() { Id = entity.Id, Name = entity.Name, DisplayName = "display:" + entity.Name };

        public IReadOnlyCollection<FieldsEntityDTO> MapToDTOs(IReadOnlyCollection<FieldsEntity> entityCollection) =>
            [.. entityCollection.Select(MapToDTO)];
    }

    private sealed class FieldsProjector : IEntityDTOProjector<FieldsEntity, FieldsEntityDTO, int>
    {
        public IQueryable<FieldsEntityDTO> ProjectTo(IQueryable<FieldsEntity> source) =>
            source.Select(e => new FieldsEntityDTO { Id = e.Id, Name = e.Name, DisplayName = "display:" + e.Name });
    }

    private static readonly List<FieldsEntity> FieldsRows =
    [
        new() { Id = 1, Name = "a", InternalNote = "secret-a" },
        new() { Id = 2, Name = "b", InternalNote = "secret-b" },
    ];

    private static EntityQueryService<FieldsEntity, FieldsEntityDTO, int> CreateFieldsSut(bool withProjector)
    {
        var repository = new Mock<IReadRepository<FieldsEntity, int>>();
        repository.SetupGet(r => r.Table).Returns(FieldsRows.AsQueryable());
        repository.SetupGet(r => r.TableNoTracking).Returns(FieldsRows.AsQueryable());

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.GetReadRepository<FieldsEntity, int>()).Returns(repository.Object);

        var navigationMetadataProvider = new Mock<INavigationMetadataProvider>();
        navigationMetadataProvider
            .Setup(p => p.BuildIncludes<FieldsEntity>(It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns(new NavigationMetadata());

        var pipeline = new EntityQueryPipeline(new InMemoryQueryableExecutor());
        var populator = new Mock<INavigationPopulator<FieldsEntity>>();

        return withProjector
            ? new EntityQueryService<FieldsEntity, FieldsEntityDTO, int>(
                unitOfWork.Object, navigationMetadataProvider.Object, pipeline, new FieldsMapper(), populator.Object, new FieldsProjector())
            : new EntityQueryService<FieldsEntity, FieldsEntityDTO, int>(
                unitOfWork.Object, navigationMetadataProvider.Object, pipeline, new FieldsMapper(), populator.Object);
    }

    [Fact]
    public async Task GetAllAsync_Projected_ShapesADtoOnlyField()
    {
        var sut = CreateFieldsSut(withProjector: true);

        var result = await sut.GetAllAsync(filters: null, fields: "Id,DisplayName");

        result.IsSuccess.Should().BeTrue("fields= names DTO properties, and the projected DTO carries DisplayName");
        result.Value!.Items.Cast<IDictionary<string, object?>>()
            .Select(shaped => shaped["displayName"])
            .Should().BeEquivalentTo(["display:a", "display:b"]);
    }

    [Fact]
    public async Task GetAllAsync_Projected_RefusesAnEntityOnlyField()
    {
        var sut = CreateFieldsSut(withProjector: true);

        var result = await sut.GetAllAsync(filters: null, fields: "InternalNote");

        result.IsFailure.Should().BeTrue("a field with no DTO accessor would shape every row into an empty object");
    }

    [Fact]
    public async Task GetAllAsync_NotProjected_RefusesAnEntityOnlyField()
    {
        var sut = CreateFieldsSut(withProjector: false);

        var result = await sut.GetAllAsync(filters: null, fields: "InternalNote");

        result.IsFailure.Should().BeTrue("a field with no DTO accessor would shape every row into an empty object");
    }

    [Fact]
    public async Task GetAllAsync_NotProjected_StillRequiresAWritableEntityProperty()
    {
        var sut = CreateFieldsSut(withProjector: false);

        var result = await sut.GetAllAsync(filters: null, fields: "DisplayName");

        result.IsFailure.Should().BeTrue(
            "the entity path narrows the entity with a MemberInit, which cannot bind a name the entity lacks");
    }

    [Fact]
    public async Task GetAllAsync_NotProjected_ShapesAFieldTheEntityAndDtoShare()
    {
        var sut = CreateFieldsSut(withProjector: false);

        var result = await sut.GetAllAsync(filters: null, fields: "Id,Name");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Cast<IDictionary<string, object?>>()
            .Select(shaped => shaped["name"])
            .Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public async Task GetByIdAsync_RefusesAnEntityOnlyField()
    {
        var sut = CreateFieldsSut(withProjector: false);

        var result = await sut.GetByIdAsync(1, fields: "InternalNote");

        result.IsFailure.Should().BeTrue("a field with no DTO accessor would shape the row into an empty object");
    }
}
