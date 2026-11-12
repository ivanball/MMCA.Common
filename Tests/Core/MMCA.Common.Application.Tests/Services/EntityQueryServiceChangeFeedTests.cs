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
/// The changed-since read: one keyset read over <c>LastModifiedOn</c> ascending with soft-deleted rows
/// in scope, an inclusive UTC lower bound ANDed with the caller's scope, and each row turned into an
/// upsert (mapped DTO) or a tombstone (no item). Rows are evaluated in memory against the captured
/// specification, which is exactly the predicate the repository would translate.
/// </summary>
public sealed class EntityQueryServiceChangeFeedTests
{
    private static readonly DateTime SinceUtc = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    public sealed class FeedEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class FeedEntityDTO : IBaseDTO<int>
    {
        public required int Id { get; init; }

        public string Name { get; set; } = string.Empty;
    }

    /// <summary>A mapper that records every entity it maps.</summary>
    private sealed class SpyMapper : IEntityDTOMapper<FeedEntity, FeedEntityDTO, int>
    {
        public List<int> MappedIds { get; } = [];

        public FeedEntityDTO MapToDTO(FeedEntity entity)
        {
            MappedIds.Add(entity.Id);
            return new FeedEntityDTO { Id = entity.Id, Name = "mapped:" + entity.Name };
        }

        public IReadOnlyCollection<FeedEntityDTO> MapToDTOs(IReadOnlyCollection<FeedEntity> entityCollection) =>
            [.. entityCollection.Select(MapToDTO)];
    }

    /// <summary>A scope that is itself a shaped query specification (it orders), which And/Or refuse.</summary>
    private sealed class OddRowsQuerySpecification : QuerySpecification<FeedEntity, int>
    {
        public OddRowsQuerySpecification() => AddOrderBy(e => e.Name);

        public override Expression<Func<FeedEntity, bool>> Criteria => e => e.Id % 2 == 1;
    }

    private readonly Mock<IReadRepository<FeedEntity, int>> _repository = new();
    private readonly SpyMapper _mapper = new();
    private readonly List<(KeysetPageRequest Request, ISpecification<FeedEntity, int>? Specification)> _repositoryCalls = [];

    private Result<KeysetCollectionResult<FeedEntity>> _repositoryResult =
        Result.Success(new KeysetCollectionResult<FeedEntity>([], nextCursor: null));

    public EntityQueryServiceChangeFeedTests() =>
        _repository
            .Setup(r => r.GetPageByCursorAsync(
                It.IsAny<KeysetPageRequest>(),
                It.IsAny<ISpecification<FeedEntity, int>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<KeysetPageRequest, ISpecification<FeedEntity, int>?, CancellationToken>(
                (request, specification, _) => _repositoryCalls.Add((request, specification)))
            .ReturnsAsync(() => _repositoryResult);

    private EntityQueryService<FeedEntity, FeedEntityDTO, int> CreateSut()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.GetReadRepository<FeedEntity, int>()).Returns(_repository.Object);

        return new EntityQueryService<FeedEntity, FeedEntityDTO, int>(
            unitOfWork.Object,
            Mock.Of<INavigationMetadataProvider>(),
            Mock.Of<IEntityQueryPipeline>(),
            _mapper,
            Mock.Of<INavigationPopulator<FeedEntity>>());
    }

    // -- Rule 5: the keyset request --
    [Fact]
    public async Task Request_SortsByLastModifiedOnAscending_PassesCursorAndSize()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();

        await sut.GetChangesSinceAsync(SinceUtc, "cursor-1", 25, cancellationToken: cts.Token);

        KeysetPageRequest request = _repositoryCalls.Should().ContainSingle().Subject.Request;
        request.SortColumn.Should().Be(nameof(AuditableBaseEntity<>.LastModifiedOn));
        request.Descending.Should().BeFalse("the feed walks forward in time, (LastModifiedOn, Id) ascending");
        request.Cursor.Should().Be("cursor-1");
        request.PageSize.Should().Be(25);
        _repository.Verify(
            r => r.GetPageByCursorAsync(It.IsAny<KeysetPageRequest>(), It.IsAny<ISpecification<FeedEntity, int>?>(), cts.Token),
            Times.Once);
    }

    // -- Rule 4: the specification --
    [Fact]
    public async Task Specification_IgnoresSoftDeleteFilter()
    {
        var sut = CreateSut();

        await sut.GetChangesSinceAsync(SinceUtc, null, 10);

        CapturedSpecification().Should().BeAssignableTo<QuerySpecification<FeedEntity, int>>()
            .Which.IgnoreQueryFilters.Should().BeTrue("a deleted row must be read to be reported as a tombstone");
    }

    [Fact]
    public async Task Since_IsInclusiveLowerBound()
    {
        var sut = CreateSut();

        await sut.GetChangesSinceAsync(SinceUtc, null, 10);

        ISpecification<FeedEntity, int> specification = CapturedSpecification();
        specification.IsSatisfiedBy(Entity(1, modified: SinceUtc)).Should().BeTrue("since is inclusive, so boundary rows come again");
        specification.IsSatisfiedBy(Entity(2, modified: SinceUtc.AddTicks(1))).Should().BeTrue();
        specification.IsSatisfiedBy(Entity(3, modified: SinceUtc.AddTicks(-1))).Should().BeFalse();
    }

    [Fact]
    public async Task NullSince_HasNoTimeBound()
    {
        var sut = CreateSut();

        await sut.GetChangesSinceAsync(null, null, 10);

        ISpecification<FeedEntity, int> specification = CapturedSpecification();
        specification.Should().BeAssignableTo<QuerySpecification<FeedEntity, int>>()
            .Which.IgnoreQueryFilters.Should().BeTrue();
        specification.IsSatisfiedBy(Entity(1, modified: new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))).Should().BeTrue();
        specification.IsSatisfiedBy(Entity(2, created: new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), modified: null))
            .Should().BeTrue("with no since every row is in the feed, whatever its timestamps");
    }

    [Fact]
    public async Task Scope_IsAndedWithSince()
    {
        var sut = CreateSut();
        var scope = new InlineSpecification<FeedEntity, int>(e => e.Id % 2 == 1);

        await sut.GetChangesSinceAsync(SinceUtc, null, 10, scope);

        ISpecification<FeedEntity, int> specification = CapturedSpecification();
        specification.IsSatisfiedBy(Entity(1, modified: SinceUtc.AddHours(1))).Should().BeTrue("in scope and changed since");
        specification.IsSatisfiedBy(Entity(2, modified: SinceUtc.AddHours(1))).Should().BeFalse("changed since but out of scope");
        specification.IsSatisfiedBy(Entity(3, modified: SinceUtc.AddHours(-1))).Should().BeFalse("in scope but unchanged since");
    }

    [Fact]
    public async Task ScopeThatIsQuerySpecification_IsAccepted()
    {
        var sut = CreateSut();

        Func<Task> act = () => sut.GetChangesSinceAsync(SinceUtc, null, 10, new OddRowsQuerySpecification());

        await act.Should().NotThrowAsync("the scope's criteria are wrapped before composition, so a shaped query specification is accepted");
        ISpecification<FeedEntity, int> specification = CapturedSpecification();
        specification.IsSatisfiedBy(Entity(1, modified: SinceUtc)).Should().BeTrue();
        specification.IsSatisfiedBy(Entity(2, modified: SinceUtc)).Should().BeFalse("the scope's criteria still apply");
    }

    // -- Rule 3: since normalized to UTC --
    [Fact]
    public async Task LocalSince_ConvertedToUtc()
    {
        // On a host whose local zone is UTC this cannot tell the two readings apart; on any other zone
        // an unconverted local value shifts the bound by the zone offset and fails one of the two checks.
        var localSince = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Local);
        DateTime expectedUtc = localSince.ToUniversalTime();
        var sut = CreateSut();

        await sut.GetChangesSinceAsync(localSince, null, 10);

        ISpecification<FeedEntity, int> specification = CapturedSpecification();
        specification.IsSatisfiedBy(Entity(1, modified: expectedUtc)).Should().BeTrue();
        specification.IsSatisfiedBy(Entity(2, modified: expectedUtc.AddTicks(-1))).Should().BeFalse();
    }

    [Fact]
    public async Task UnspecifiedSince_TreatedAsUtc()
    {
        var unspecified = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var asUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var sut = CreateSut();

        await sut.GetChangesSinceAsync(unspecified, null, 10);

        ISpecification<FeedEntity, int> specification = CapturedSpecification();
        specification.IsSatisfiedBy(Entity(1, modified: asUtc)).Should().BeTrue("an unspecified kind is taken as UTC, not shifted");
        specification.IsSatisfiedBy(Entity(2, modified: asUtc.AddTicks(-1))).Should().BeFalse();
    }

    // -- Rules 6, 7 and 8: tombstones, upserts, ChangedOn --
    [Fact]
    public async Task DeletedRow_IsTombstoneWithoutItem()
    {
        FeedEntity deleted = Entity(7, modified: SinceUtc.AddMinutes(5), name: "secret");
        deleted.Delete().IsSuccess.Should().BeTrue();
        _repositoryResult = Result.Success(new KeysetCollectionResult<FeedEntity>([deleted], nextCursor: null));
        var sut = CreateSut();

        var result = await sut.GetChangesSinceAsync(SinceUtc, null, 10);

        result.IsSuccess.Should().BeTrue();
        EntityChange<FeedEntityDTO, int> change = result.Value!.Items.Should().ContainSingle().Subject;
        change.Id.Should().Be(7);
        change.IsDeleted.Should().BeTrue();
        change.Item.Should().BeNull("a tombstone carries no field of the deleted row");
        change.ChangedOn.Should().Be(SinceUtc.AddMinutes(5));
        _mapper.MappedIds.Should().NotContain(7, "a deleted row is never mapped, so none of its fields can leak");
    }

    [Fact]
    public async Task LiveRow_CarriesMappedDto()
    {
        FeedEntity first = Entity(3, modified: SinceUtc, name: "c");
        FeedEntity tombstone = Entity(1, modified: SinceUtc.AddMinutes(1), name: "a");
        tombstone.Delete().IsSuccess.Should().BeTrue();
        FeedEntity last = Entity(2, modified: SinceUtc.AddMinutes(2), name: "b");
        _repositoryResult = Result.Success(new KeysetCollectionResult<FeedEntity>([first, tombstone, last], nextCursor: "next-page"));
        var sut = CreateSut();

        var result = await sut.GetChangesSinceAsync(SinceUtc, null, 10);

        result.IsSuccess.Should().BeTrue();
        List<EntityChange<FeedEntityDTO, int>> changes = [.. result.Value!.Items];
        changes.Select(c => c.Id).Should().Equal([3, 1, 2], "changes keep the repository's page order");
        changes.Select(c => c.IsDeleted).Should().Equal(false, true, false);
        changes[0].Item.Should().NotBeNull();
        changes[0].Item!.Name.Should().Be("mapped:c", "an upsert carries the normal mapped DTO");
        changes[2].Item!.Id.Should().Be(2);
        changes[0].ChangedOn.Should().Be(SinceUtc);
        result.Value.NextCursor.Should().Be("next-page");
    }

    [Fact]
    public async Task ChangedOn_FallsBackToCreatedOn()
    {
        var createdOn = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        _repositoryResult = Result.Success(new KeysetCollectionResult<FeedEntity>(
            [Entity(4, created: createdOn, modified: null)], nextCursor: null));
        var sut = CreateSut();

        var result = await sut.GetChangesSinceAsync(null, null, 10);

        result.Value!.Items.Should().ContainSingle().Which.ChangedOn.Should().Be(createdOn);
    }

    // -- Rule 5: repository failures pass through --
    [Fact]
    public async Task RepositoryFailure_IsPropagated()
    {
        var invalidCursor = Error.Validation(
            "Error.InvalidCursor", "The supplied pagination cursor isn't valid.", "GetPageByCursorAsync", nameof(FeedEntity));
        _repositoryResult = Result.Failure<KeysetCollectionResult<FeedEntity>>(invalidCursor);
        var sut = CreateSut();

        var result = await sut.GetChangesSinceAsync(SinceUtc, "garbage", 10);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("Error.InvalidCursor");
        result.Errors[0].Type.Should().Be(ErrorType.Validation);
    }

    // -- Rule 11: the default interface member --
    [Fact]
    public async Task DefaultInterfaceMember_ReturnsNotSupportedFailure()
    {
        IEntityQueryService<FeedEntity, FeedEntityDTO, int> legacy = new LegacyQueryService();

        var result = await legacy.GetChangesSinceAsync(SinceUtc, null, 10);

        IEntityQueryService<FeedEntity, FeedEntityDTO, int>.ChangeFeedNotSupportedErrorCode
            .Should().Be("Query.ChangeFeedNotSupported");
        result.IsFailure.Should().BeTrue();
        Error error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(IEntityQueryService<FeedEntity, FeedEntityDTO, int>.ChangeFeedNotSupportedErrorCode);
        error.Type.Should().Be(ErrorType.Unexpected, "a service that cannot serve the feed is a server-side gap: 500");
    }

    // -- Helpers --
    private ISpecification<FeedEntity, int> CapturedSpecification()
    {
        ISpecification<FeedEntity, int>? specification = _repositoryCalls.Should().ContainSingle().Subject.Specification;
        specification.Should().NotBeNull("the feed always reads through its own specification");
        return specification;
    }

    private static FeedEntity Entity(int id, DateTime? modified, DateTime? created = null, string name = "row")
    {
        var entity = new FeedEntity { Id = id, Name = name };
        SetAuditField(entity, nameof(AuditableBaseEntity<>.CreatedOn), created ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SetAuditField(entity, nameof(AuditableBaseEntity<>.LastModifiedOn), modified);
        return entity;
    }

    /// <summary>Audit fields have private setters stamped by the save interceptor; tests stamp them directly.</summary>
    private static void SetAuditField(FeedEntity entity, string propertyName, object? value) =>
        typeof(AuditableBaseEntity<int>).GetProperty(propertyName)!.SetValue(entity, value);

    /// <summary>An implementation written before the change-feed member existed.</summary>
    private sealed class LegacyQueryService : IEntityQueryService<FeedEntity, FeedEntityDTO, int>
    {
        public IEntityDTOMapper<FeedEntity, FeedEntityDTO, int> DTOMapper => throw new NotSupportedException();

        public Task<Result<PagedCollectionResult<object>>> GetAllAsync(
            bool includeFKs = false,
            bool includeChildren = false,
            ISpecification<FeedEntity, int>? specification = null,
            string? fields = null,
            bool asTracking = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<PagedCollectionResult<object>>> GetAllAsync(
            bool includeFKs = false,
            bool includeChildren = false,
            ISpecification<FeedEntity, int>? specification = null,
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
            Expression<Func<FeedEntity, bool>>? where = null,
            bool asTracking = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<FeedEntity>> GetEntityByIdAsync(
            string idValue,
            string? idField = null,
            bool includeFKs = false,
            bool includeChildren = false,
            ISpecification<FeedEntity, int>? specification = null,
            string? fields = null,
            bool asTracking = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<object>> GetByIdAsync(
            int id,
            bool includeFKs = false,
            bool includeChildren = false,
            ISpecification<FeedEntity, int>? specification = null,
            string? fields = null,
            bool asTracking = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> ExistsAsync(
            Expression<Func<FeedEntity, bool>> where,
            bool ignoreQueryFilters = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
