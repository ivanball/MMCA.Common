using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.Interfaces.Navigation;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.DTOs;
using Moq;

namespace MMCA.Common.Application.Tests.Services.Query;

/// <summary>
/// SEC-Common-24: <c>?nameProperty=</c> used to project ANY public entity property into the lookup
/// list, for every row, because validation only checked that the name existed on the ENTITY. The
/// allow-list is now the response contract, and a subclass can narrow it further.
/// </summary>
public sealed class EntityQueryServiceContractTests
{
    public sealed class AccountEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>Credential material an adopter's entity may carry; no DTO exposes it.</summary>
        public string SecurityStamp { get; set; } = string.Empty;
    }

    public sealed class AccountDTO : IBaseDTO<int>
    {
        public required int Id { get; init; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class NarrowedQueryService(
        IUnitOfWork unitOfWork,
        INavigationMetadataProvider navigationMetadataProvider,
        IEntityQueryPipeline queryPipeline,
        IEntityDTOMapper<AccountEntity, AccountDTO, int> dtoMapper,
        INavigationPopulator<AccountEntity> navigationPopulator)
        : EntityQueryService<AccountEntity, AccountDTO, int>(
            unitOfWork,
            navigationMetadataProvider,
            queryPipeline,
            dtoMapper,
            navigationPopulator)
    {
        protected override QueryFieldContract? LookupNameContract => QueryFieldContract.ForNames(["Name"]);
    }

    private static Mock<IReadRepository<AccountEntity, int>> Repository()
    {
        var repository = new Mock<IReadRepository<AccountEntity, int>>();
        repository
            .Setup(x => x.GetAllForLookupAsync(
                It.IsAny<string>(),
                It.IsAny<Expression<Func<AccountEntity, bool>>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BaseLookup<int> { Id = 1, Name = "row" }]);
        return repository;
    }

    private static (EntityQueryService<AccountEntity, AccountDTO, int> Sut, Mock<IReadRepository<AccountEntity, int>> Repository) Build(bool narrowed = false)
    {
        var repository = Repository();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.GetReadRepository<AccountEntity, int>()).Returns(repository.Object);

        EntityQueryService<AccountEntity, AccountDTO, int> sut = narrowed
            ? new NarrowedQueryService(
                unitOfWork.Object,
                Mock.Of<INavigationMetadataProvider>(),
                Mock.Of<IEntityQueryPipeline>(),
                Mock.Of<IEntityDTOMapper<AccountEntity, AccountDTO, int>>(),
                Mock.Of<INavigationPopulator<AccountEntity>>())
            : new EntityQueryService<AccountEntity, AccountDTO, int>(
                unitOfWork.Object,
                Mock.Of<INavigationMetadataProvider>(),
                Mock.Of<IEntityQueryPipeline>(),
                Mock.Of<IEntityDTOMapper<AccountEntity, AccountDTO, int>>(),
                Mock.Of<INavigationPopulator<AccountEntity>>());

        return (sut, repository);
    }

    [Fact]
    public async Task GetAllForLookupAsync_RefusesAnEntityOnlyColumn()
    {
        var (sut, repository) = Build();

        var result = await sut.GetAllForLookupAsync("SecurityStamp", cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        repository.Verify(
            r => r.GetAllForLookupAsync(It.IsAny<string>(), It.IsAny<Expression<Func<AccountEntity, bool>>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the query must not reach the database at all");
    }

    [Fact]
    public async Task GetAllForLookupAsync_StillProjectsAContractColumn()
    {
        var (sut, _) = Build();

        var result = await sut.GetAllForLookupAsync("Name", cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetAllForLookupAsync_RefusesAnAuditColumnTheDtoDoesNotDeclare()
    {
        var (sut, _) = Build();

        var result = await sut.GetAllForLookupAsync("CreatedBy", cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task LookupNameContract_CanBeNarrowedBelowTheDto()
    {
        var (sut, _) = Build(narrowed: true);

        var allowed = await sut.GetAllForLookupAsync("Name", cancellationToken: TestContext.Current.CancellationToken);
        var refused = await sut.GetAllForLookupAsync("Id", cancellationToken: TestContext.Current.CancellationToken);

        allowed.IsSuccess.Should().BeTrue();
        refused.IsFailure.Should().BeTrue();
    }
}
