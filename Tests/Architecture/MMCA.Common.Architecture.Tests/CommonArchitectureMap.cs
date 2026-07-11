using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// The architecture map for MMCA.Common — a module-less framework, so every layer is a framework layer.
/// One anchor type per package pins the assembly (mirrors the old <c>PackageAssemblies</c> helper).
/// <para>
/// <c>MMCA.Common.UI.Maui</c> (ADR-042) is deliberately ABSENT: its four MAUI TFM assemblies cannot
/// load in this ubuntu-run net10.0 test process, so its layer boundary (UI + Shared only) is enforced
/// at compile time instead, by <c>EnforceUIMauiLayerBoundary</c> in
/// <c>Source/Build/MMCA.Common.LayerEnforcement.targets</c> and the windows <c>build-maui</c> CI job.
/// </para>
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
