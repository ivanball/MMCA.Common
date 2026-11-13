using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.Interfaces.Navigation;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using Moq;

namespace MMCA.Common.Application.Tests.Services;

/// <summary>
/// The query service's keyset read: the sort column is validated against the response contract and
/// mapped before the repository sees it, the repository's page is mapped in order with its cursor,
/// and an implementation that predates the member answers a not-supported failure.
/// </summary>
public sealed class EntityQueryServiceCursorTests
{
    public sealed class CursorEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>Entity-only column: the DTO deliberately does not carry it.</summary>
        public string Secret { get; set; } = string.Empty;
    }

    public sealed class CursorEntityDTO : IBaseDTO<int>
    {
        public required int Id { get; init; }

        public string Name { get; set; } = string.Empty;

        /// <summary>DTO-only name that the service maps to the entity's Name.</summary>
        public string DisplayName { get; set; } = string.Empty;
    }

    /// <summary>A mapper that records what it was handed, so a test can prove what was mapped.</summary>
    private sealed class SpyMapper : IEntityDTOMapper<CursorEntity, CursorEntityDTO, int>
    {
        public List<int> MappedIds { get; } = [];

        public CursorEntityDTO MapToDTO(CursorEntity entity)
        {
            MappedIds.Add(entity.Id);
            return new CursorEntityDTO { Id = entity.Id, Name = "mapped:" + entity.Name };
        }

        public IReadOnlyCollection<CursorEntityDTO> MapToDTOs(IReadOnlyCollection<CursorEntity> entityCollection) =>
            [.. entityCollection.Select(MapToDTO)];
    }

    /// <summary>A service whose DTO name DisplayName maps to the entity's Name.</summary>
    private sealed class MappedCursorQueryService(
        IUnitOfWork unitOfWork,
        INavigationMetadataProvider navigationMetadataProvider,
        IEntityQueryPipeline queryPipeline,
        IEntityDTOMapper<CursorEntity, CursorEntityDTO, int> dtoMapper,
        INavigationPopulator<CursorEntity> navigationPopulator,
        IReadOnlyDictionary<string, string> map)
        : EntityQueryService<CursorEntity, CursorEntityDTO, int>(unitOfWork, navigationMetadataProvider, queryPipeline, dtoMapper, navigationPopulator)
    {
        protected override IReadOnlyDictionary<string, string> DTOToEntityPropertyMap => map;
    }

    private readonly Mock<IReadRepository<CursorEntity, int>> _repository = new();
    private readonly Mock<INavigationMetadataProvider> _navigationMetadataProvider = new(MockBehavior.Strict);
    private readonly Mock<IEntityQueryPipeline> _pipeline = new(MockBehavior.Strict);
    private readonly SpyMapper _mapper = new();
    private readonly List<(KeysetPageRequest Request, ISpecification<CursorEntity, int>? Specification)> _repositoryCalls = [];

    private Result<KeysetCollectionResult<CursorEntity>> _repositoryResult =
        Result.Success(new KeysetCollectionResult<CursorEntity>([], nextCursor: null));

    public EntityQueryServiceCursorTests() =>
        _repository
            .Setup(r => r.GetPageByCursorAsync(
                It.IsAny<KeysetPageRequest>(),
                It.IsAny<ISpecification<CursorEntity, int>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<KeysetPageRequest, ISpecification<CursorEntity, int>?, CancellationToken>(
                (request, specification, _) => _repositoryCalls.Add((request, specification)))
            .ReturnsAsync(() => _repositoryResult);

    private Mock<IUnitOfWork> UnitOfWork()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.GetReadRepository<CursorEntity, int>()).Returns(_repository.Object);
        return unitOfWork;
    }

    private EntityQueryService<CursorEntity, CursorEntityDTO, int> CreateSut() =>
        new(UnitOfWork().Object, _navigationMetadataProvider.Object, _pipeline.Object, _mapper, Mock.Of<INavigationPopulator<CursorEntity>>());

    private MappedCursorQueryService CreateMappedSut(string dtoName, string entityPath) =>
        new(
            UnitOfWork().Object,
            _navigationMetadataProvider.Object,
            _pipeline.Object,
            _mapper,
            Mock.Of<INavigationPopulator<CursorEntity>>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [dtoName] = entityPath });

    // -- Rule 8: sort column validation, before the repository --
    [Fact]
    public async Task UnknownSortColumn_FailsValidation_NoRepositoryCall()
    {
        var sut = CreateSut();

        var result = await sut.GetPageByCursorAsync(new KeysetPageRequest(10, "NotAColumn"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().NotBeEmpty().And.AllSatisfy(error =>
        {
            error.Code.Should().Be("Error.InvalidEntityField");
            error.Source.Should().Be(nameof(IEntityQueryService<,,>.GetPageByCursorAsync));
            error.Target.Should().Be(nameof(CursorEntity));
        });
        _repositoryCalls.Should().BeEmpty("a refused sort column never reaches the database");
    }

    [Fact]
    public async Task SortColumnOutsideFieldContract_Fails()
    {
        var sut = CreateSut();

        var result = await sut.GetPageByCursorAsync(new KeysetPageRequest(10, "Secret"));

        result.IsFailure.Should().BeTrue("an entity-only column would disclose its order to a caller who cannot see it");
        result.Errors.Should().AllSatisfy(error =>
        {
            error.Source.Should().Be(nameof(IEntityQueryService<,,>.GetPageByCursorAsync));
            error.Target.Should().Be(nameof(CursorEntity));
        });
        _repositoryCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ContractSortColumn_ReachesTheRepository()
    {
        var sut = CreateSut();

        var result = await sut.GetPageByCursorAsync(new KeysetPageRequest(10, "Name"));

        result.IsSuccess.Should().BeTrue();
        _repositoryCalls.Should().ContainSingle().Which.Request.SortColumn.Should().Be("Name");
    }

    // -- Rule 9: map translation --
    [Fact]
    public async Task MappedSortColumn_IsTranslated_BeforeRepositoryCall()
    {
        var sut = CreateMappedSut("DisplayName", nameof(CursorEntity.Name));

        var result = await sut.GetPageByCursorAsync(new KeysetPageRequest(10, "displayNAME", descending: true, cursor: "c1"));

        result.IsSuccess.Should().BeTrue();
        KeysetPageRequest request = _repositoryCalls.Should().ContainSingle().Subject.Request;
        request.SortColumn.Should().Be(nameof(CursorEntity.Name), "the repository resolves entity properties, never DTO names");
        request.Descending.Should().BeTrue("translation keeps the rest of the request");
        request.Cursor.Should().Be("c1");
        request.PageSize.Should().Be(10);
    }

    [Fact]
    public async Task MappedPath_IsPassedToTheRepository_WhoseFailureIsReturned()
    {
        var sut = CreateMappedSut("ParentName", "Parent.Name");
        Error repositoryError = Error.InvalidEntityField with
        {
            Message = "Sort column 'Parent.Name' does not exist on type 'CursorEntity'.",
            Source = "GetPageByCursorAsync",
            Target = nameof(CursorEntity),
        };
        _repositoryResult = Result.Failure<KeysetCollectionResult<CursorEntity>>(repositoryError);

        var result = await sut.GetPageByCursorAsync(new KeysetPageRequest(10, "ParentName"));

        _repositoryCalls.Should().ContainSingle().Which.Request.SortColumn.Should().Be("Parent.Name");
        result.IsFailure.Should().BeTrue("a navigation path is not a keyset sort key; the repository refuses it");
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("Error.InvalidEntityField");
    }

    // -- Rule 11: mapping in order, cursor passed through --
    [Fact]
    public async Task Success_MapsEntitiesToDtos_InOrder_PassesNextCursor()
    {
        _repositoryResult = Result.Success(new KeysetCollectionResult<CursorEntity>(
            [
                new CursorEntity { Id = 3, Name = "c" },
                new CursorEntity { Id = 1, Name = "a" },
                new CursorEntity { Id = 2, Name = "b" },
            ],
            nextCursor: "opaque-next"));
        var sut = CreateSut();

        var result = await sut.GetPageByCursorAsync(new KeysetPageRequest(3));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Select(dto => dto.Id).Should().Equal([3, 1, 2], "repository order is the keyset order");
        result.Value.Items.Select(dto => dto.Name).Should().Equal("mapped:c", "mapped:a", "mapped:b");
        result.Value.NextCursor.Should().Be("opaque-next");
        _mapper.MappedIds.Should().Equal(3, 1, 2);
    }

    // -- Rule 10: repository failures, including a malformed cursor --
    [Fact]
    public async Task RepositoryFailure_IsPropagated()
    {
        var invalidCursor = Error.Validation(
            "Error.InvalidCursor", "The supplied pagination cursor isn't valid.", "GetPageByCursorAsync", nameof(CursorEntity));
        _repositoryResult = Result.Failure<KeysetCollectionResult<CursorEntity>>(invalidCursor);
        var sut = CreateSut();

        var result = await sut.GetPageByCursorAsync(new KeysetPageRequest(10, cursor: "not-a-cursor!!"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("Error.InvalidCursor");
        result.Errors[0].Type.Should().Be(ErrorType.Validation, "a malformed cursor is the caller's mistake: 400, not 500");
        _mapper.MappedIds.Should().BeEmpty();
    }

    // -- Rule 5 (service half) and rule 12: the specification only --
    [Fact]
    public async Task Specification_IsPassedThrough()
    {
        var specification = new InlineSpecification<CursorEntity, int>(e => e.Id > 1);
        using var cts = new CancellationTokenSource();
        var sut = CreateSut();

        await sut.GetPageByCursorAsync(new KeysetPageRequest(10), specification, cts.Token);

        _repositoryCalls.Should().ContainSingle().Which.Specification.Should().BeSameAs(specification);
        _repository.Verify(
            r => r.GetPageByCursorAsync(It.IsAny<KeysetPageRequest>(), It.IsAny<ISpecification<CursorEntity, int>?>(), cts.Token),
            Times.Once);
    }

    [Fact]
    public async Task NoIncludesNoPipeline_TheRepositoryKeysetReadIsTheOnlyDataPath()
    {
        var sut = CreateSut();

        var result = await sut.GetPageByCursorAsync(new KeysetPageRequest(10, "Name"));

        result.IsSuccess.Should().BeTrue();

        // Both collaborators are strict mocks with no setups: any include build or pipeline run throws.
        _navigationMetadataProvider.VerifyNoOtherCalls();
        _pipeline.VerifyNoOtherCalls();
        _repositoryCalls.Should().ContainSingle();
    }

    // -- Rule 13: the default interface member --
    [Fact]
    public async Task DefaultInterfaceMember_ReturnsNotSupportedFailure()
    {
        IEntityQueryService<CursorEntity, CursorEntityDTO, int> legacy = new LegacyQueryService();

        var result = await legacy.GetPageByCursorAsync(new KeysetPageRequest(10));

        IEntityQueryService<CursorEntity, CursorEntityDTO, int>.CursorPagingNotSupportedErrorCode
            .Should().Be("Query.CursorPagingNotSupported");
        result.IsFailure.Should().BeTrue();
        Error error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(IEntityQueryService<CursorEntity, CursorEntityDTO, int>.CursorPagingNotSupportedErrorCode);
        error.Type.Should().Be(ErrorType.Unexpected, "an implementation that cannot page by cursor is a server-side gap: 500");
        error.Source.Should().Be(nameof(IEntityQueryService<,,>.GetPageByCursorAsync));
        error.Target.Should().Be(nameof(CursorEntity));
    }

    /// <summary>
    /// An implementation written before the keyset member existed: it compiles unchanged and inherits
    /// the default body.
    /// </summary>
    private sealed class LegacyQueryService : IEntityQueryService<CursorEntity, CursorEntityDTO, int>
    {
        public IEntityDTOMapper<CursorEntity, CursorEntityDTO, int> DTOMapper => throw new NotSupportedException();

        public Task<Result<PagedCollectionResult<object>>> GetAllAsync(
            bool includeFKs = false,
            bool includeChildren = false,
            ISpecification<CursorEntity, int>? specification = null,
            string? fields = null,
            bool asTracking = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<PagedCollectionResult<object>>> GetAllAsync(
            bool includeFKs = false,
            bool includeChildren = false,
            ISpecification<CursorEntity, int>? specification = null,
            Dictionary<string, (string Operator, string Value)>? filters = null,
            string? sortColumn = null,
            string? sortDirection = null,
            string? fields = null,
            int? pageNumber = null,
            int? pageSize = null,
            bool asTracking = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<IReadOnlyCollection<BaseLookup<int>>>> GetAllForLookupAsync(
            string nameProperty,
            Expression<Func<CursorEntity, bool>>? where = null,
            bool asTracking = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<CursorEntity>> GetEntityByIdAsync(
            string idValue,
            string? idField = null,
            bool includeFKs = false,
            bool includeChildren = false,
            ISpecification<CursorEntity, int>? specification = null,
            string? fields = null,
            bool asTracking = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<object>> GetByIdAsync(
            int id,
            bool includeFKs = false,
            bool includeChildren = false,
            ISpecification<CursorEntity, int>? specification = null,
            string? fields = null,
            bool asTracking = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> ExistsAsync(
            Expression<Func<CursorEntity, bool>> where,
            bool ignoreQueryFilters = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
