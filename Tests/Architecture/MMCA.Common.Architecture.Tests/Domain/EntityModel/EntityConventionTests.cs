using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Domain.EntityModel;

/// <summary>
/// DDD entity rules (rubric section 4), driven by the shared <see cref="EntityConventionTestsBase"/>
/// over the framework's own Domain assembly: the Notifications aggregates are
/// sealed, built through a <c>Result</c>-returning <c>Create</c> factory with no public constructor,
/// and expose no public setter. <see cref="FrameworkModuleArchitectureMap"/> registers the framework
/// assemblies as a module so the module-scoped rules are not vacuous here.
/// </summary>
public sealed class EntityConventionTests : EntityConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new FrameworkModuleArchitectureMap();
}
