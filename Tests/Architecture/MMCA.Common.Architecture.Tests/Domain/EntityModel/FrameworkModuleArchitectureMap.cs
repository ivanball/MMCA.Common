using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Domain.EntityModel;

/// <summary>
/// A map that declares the framework's own Shared, Domain and Application assemblies as one business
/// module, so the shared DDD rules that scope to <see cref="IArchitectureMap.ModuleDomain"/>,
/// <see cref="IArchitectureMap.ModuleShared"/> and <see cref="IArchitectureMap.ModuleApplication"/>
/// (sealed entities, no public setters, no public aggregate constructors, immutable DTOs, commands,
/// queries and domain events) actually run over the aggregates MMCA.Common ships: the Notifications
/// family (<c>PushNotification</c>, <c>UserNotification</c>).
/// <see cref="CommonArchitectureMap"/> declares the same assemblies as framework layers, where those
/// module-scoped rules are vacuous. Infrastructure stays a framework layer: the rules only read it to
/// prove entities and DTOs do not live there.
/// </summary>
internal sealed class FrameworkModuleArchitectureMap : ArchitectureMapBase
{
    /// <summary>The label the framework assemblies are registered under.</summary>
    public const string ModuleName = "Framework";

    public override string RepoToken => "MMCA.Common";

    protected override IEnumerable<LayerRef> DefineLayers() =>
    [
        Module(ModuleName, Layer.Shared, typeof(Common.Shared.Abstractions.Result).Assembly),
        Module(ModuleName, Layer.Domain, typeof(Common.Domain.Entities.BaseEntity<>).Assembly),
        Module(ModuleName, Layer.Application, typeof(Common.Application.Services.DomainEventDispatcher).Assembly),
        Framework(Layer.Infrastructure, typeof(Common.Infrastructure.Persistence.DbContexts.ApplicationDbContext).Assembly),
    ];
}
