using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Layering;

/// <summary>
/// <c>[ServiceContract]</c> purity rule (a published contract type must not reach into the producing
/// service's Domain/Application/Infrastructure), driven by the shared
/// <see cref="ServiceContractPurityTestsBase"/>. MMCA.Common marks no type today, so the framework run
/// is a ratchet rather than an assertion; see the base for why the rule is attribute-driven.
/// </summary>
public sealed class ServiceContractPurityTests : ServiceContractPurityTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
