namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Encapsulation fitness function for the <c>[ServiceContract]</c> boundary: the interface is the
/// published surface of a service (ADR-007), so the concrete class serving it must not be public.
/// A public implementation lets a consumer reference, construct or subclass the type, and each of
/// those references is a coupling the extraction has to sever later: an interface can be answered
/// over a wire, a class cannot.
/// </summary>
/// <remarks>
/// The twin of <see cref="ServiceContractPurityTestsBase"/>, guarding the same boundary from the
/// other side: purity keeps the producer's internals out of the contract, this keeps the contract's
/// implementation out of the consumer's reach.
/// <para>
/// Opt-in and honest about the vacuous case, like its twin: a repo that marks no interface yet
/// (MMCA.Common itself marks none) passes without asserting anything. The value is the ratchet, with
/// no test to remember to write when the first contract appears.
/// </para>
/// </remarks>
public abstract class ContractImplementationTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Type full names (<c>MMCA.X.Infrastructure.Contracts.DefaultPricingService</c>) or namespace
    /// prefixes (<c>MMCA.X.Infrastructure.Contracts</c>) where a public implementation is the
    /// deliberate requirement: a shipped default a consumer is meant to construct, or a test double
    /// in a testing package. Empty by default, which requires every implementation to be non-public.
    /// </summary>
    protected virtual IReadOnlyCollection<string> AllowedPublicImplementations => [];

    [Fact]
    public void ServiceContractImplementations_ShouldNotBe_Public() =>
        ArchitectureRules.ServiceContractImplementationsAreNotPublic(Map, AllowedPublicImplementations);
}
