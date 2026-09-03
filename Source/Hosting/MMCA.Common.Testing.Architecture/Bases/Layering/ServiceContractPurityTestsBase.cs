namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Purity fitness function for the <c>[ServiceContract]</c> wire surface: a type marked as part of a
/// published service contract (ADR-007) must not depend on the producing service's Domain, Application
/// or Infrastructure, so a consumer takes the contract package without taking the producer's internals.
/// </summary>
/// <remarks>
/// Attribute-driven, not <see cref="Layer.Contracts"/>-driven: no repo registers that layer in its map
/// today, so a layer-iterating rule would pass vacuously forever. This base scans every assembly the map
/// registers for types carrying the marker, wherever those types live.
/// <para>
/// Honest about the vacuous case, like <see cref="ProtoContractTestsBase"/>: a repo that marks no type
/// yet (MMCA.Common itself ships no <c>[ServiceContract]</c> type) passes without asserting anything.
/// The value is the ratchet: the invariant is enforced from the first marked type onward, with no test
/// to remember to write. It complements, and does not replace, the transport- and layer-purity rules
/// (ADR-015) that guard the same boundary from the layer side.
/// </para>
/// </remarks>
public abstract class ServiceContractPurityTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void ServiceContracts_ShouldNotDependOn_ServiceInternals() =>
        ArchitectureRules.ServiceContractsDoNotDependOnServiceInternals(Map);
}
