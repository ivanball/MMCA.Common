using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Domain.EntityModel;

/// <summary>
/// Immutability rules (rubric section 4), driven by the shared <see cref="ImmutabilityTestsBase"/> over
/// the framework's own assemblies: DTOs, command/query messages, domain events, integration events
/// and value objects expose no public mutable setter. <see cref="FrameworkModuleArchitectureMap"/>
/// registers the framework assemblies as a module so the module-scoped rules are not vacuous here.
/// </summary>
public sealed class ImmutabilityTests : ImmutabilityTestsBase
{
    protected override IArchitectureMap Map { get; } = new FrameworkModuleArchitectureMap();
}
