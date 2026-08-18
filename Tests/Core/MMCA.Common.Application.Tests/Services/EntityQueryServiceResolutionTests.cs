using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.DTOs;
using Moq;

namespace MMCA.Common.Application.Tests.Services;

/// <summary>
/// Proves the two-constructor arrangement actually resolves under
/// <c>Microsoft.Extensions.DependencyInjection</c>: the longer constructor when a projector is
/// registered, the shorter one when it is not. It has no notion of an optional dependency, so a
/// single constructor naming an unregistered service would simply fail to resolve.
/// </summary>
public sealed class EntityQueryServiceResolutionTests
{
    public sealed class ResolvedEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class ResolvedEntityDTO : IBaseDTO<int>
    {
        public required int Id { get; init; }
    }

    private sealed class ResolvedProjector : IEntityDTOProjector<ResolvedEntity, ResolvedEntityDTO, int>
    {
        public IQueryable<ResolvedEntityDTO> ProjectTo(IQueryable<ResolvedEntity> source) =>
            source.Select(e => new ResolvedEntityDTO { Id = e.Id });
    }

    private static ServiceCollection BaseServices()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.GetReadRepository<ResolvedEntity, int>())
            .Returns(new Mock<IReadRepository<ResolvedEntity, int>>().Object);

        var services = new ServiceCollection();
        services.AddSingleton(unitOfWork.Object);
        services.AddSingleton(Mock.Of<INavigationMetadataProvider>());
        services.AddSingleton(Mock.Of<IEntityQueryPipeline>());
        services.AddSingleton(Mock.Of<IEntityDTOMapper<ResolvedEntity, ResolvedEntityDTO, int>>());
        services.AddSingleton(Mock.Of<INavigationPopulator<ResolvedEntity>>());
        services.AddScoped<IEntityQueryService<ResolvedEntity, ResolvedEntityDTO, int>,
            EntityQueryService<ResolvedEntity, ResolvedEntityDTO, int>>();
        return services;
    }

    [Fact]
    public void TheQueryService_ResolvesWithoutAProjector()
    {
        using var provider = BaseServices().BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IEntityQueryService<ResolvedEntity, ResolvedEntityDTO, int>>();

        resolved.Should().NotBeNull();
    }

    [Fact]
    public void TheQueryService_ResolvesWithAProjector()
    {
        var services = BaseServices();
        services.AddScoped<IEntityDTOProjector<ResolvedEntity, ResolvedEntityDTO, int>, ResolvedProjector>();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IEntityQueryService<ResolvedEntity, ResolvedEntityDTO, int>>();

        resolved.Should().NotBeNull("the longer constructor is a strict superset, so there is no ambiguity");
    }

    [Fact]
    public void ScanModuleApplicationServices_RegistersProjectorsBesideMappers()
    {
        var services = new ServiceCollection();

        services.ScanModuleApplicationServices<ResolvedProjectorMarker>();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEntityDTOProjector<ResolvedEntity, ResolvedEntityDTO, int>));
    }

    /// <summary>
    /// A public projector in this assembly, so the module scan has something to find. It is separate
    /// from the private one above because Scrutor only registers public types.
    /// </summary>
    public sealed class ResolvedProjectorMarker : IEntityDTOProjector<ResolvedEntity, ResolvedEntityDTO, int>
    {
        /// <inheritdoc />
        public IQueryable<ResolvedEntityDTO> ProjectTo(IQueryable<ResolvedEntity> source) =>
            source.Select(e => new ResolvedEntityDTO { Id = e.Id });
    }
}
