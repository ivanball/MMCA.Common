using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// The architecture map for MMCA.Common — a module-less framework, so every layer is a framework layer.
/// One anchor type per package pins the assembly (mirrors the old <c>PackageAssemblies</c> helper).
/// </summary>
internal sealed class CommonArchitectureMap : ArchitectureMapBase
{
    public override string RepoToken => "MMCA.Common";

    protected override IEnumerable<LayerRef> DefineLayers() =>
    [
        Framework(Layer.Shared, typeof(Common.Shared.Abstractions.Result).Assembly),
        Framework(Layer.Domain, typeof(Common.Domain.Entities.BaseEntity<>).Assembly),
        Framework(Layer.Application, typeof(Common.Application.Services.DomainEventDispatcher).Assembly),
        Framework(Layer.Infrastructure, typeof(Common.Infrastructure.Persistence.DbContexts.ApplicationDbContext).Assembly),
        Framework(Layer.Api, typeof(Common.API.Controllers.ApiControllerBase).Assembly),
        Framework(Layer.Grpc, typeof(Common.Grpc.ResultGrpcExtensions).Assembly),
        Framework(Layer.Ui, typeof(Common.UI.UISharedAssemblyReference).Assembly),
    ];
}
