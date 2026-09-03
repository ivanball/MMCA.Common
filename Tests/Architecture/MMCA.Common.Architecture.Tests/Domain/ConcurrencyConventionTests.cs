using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Domain;

/// <summary>
/// Optimistic-concurrency convention (no <c>*UpdateRequest</c> carries a token in its body), driven
/// by the shared <see cref="ConcurrencyConventionTestsBase"/>. MMCA.Common is module-less, so the
/// framework run is a ratchet rather than an assertion: it fails the moment the framework itself
/// grows an update request that reintroduces a body token, and it is the same rule Store and ADC
/// subclass over their own modules.
/// </summary>
public sealed class ConcurrencyConventionTests : ConcurrencyConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
