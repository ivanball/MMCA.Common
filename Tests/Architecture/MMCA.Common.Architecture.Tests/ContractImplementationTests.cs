using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// <c>[ServiceContract]</c> encapsulation rule (the class serving a published contract interface must
/// not be public), driven by the shared <see cref="ContractImplementationTestsBase"/>. MMCA.Common
/// marks no interface today, so the framework run is a ratchet rather than an assertion; see the base
/// for why the rule is attribute-driven.
/// </summary>
public sealed class ContractImplementationTests : ContractImplementationTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
