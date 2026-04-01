using AwesomeAssertions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.DTOs;
using Moq;

namespace MMCA.Common.Application.Tests.Services;

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
}
