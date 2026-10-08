using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Domain;

/// <summary>
/// Optimistic-concurrency convention (no <c>*UpdateRequest</c> carries a token in its body), driven
/// by the shared <see cref="ConcurrencyConventionTestsBase"/>. MMCA.Common is module-less and the
/// rule scans only module Application assemblies, while <see cref="CommonArchitectureMap"/> declares
/// framework layers alone, so this run is vacuous here: it would not catch a framework update
/// request that reintroduces a body token. It exists to exercise the same rule Store and ADC
/// subclass over their own modules.
/// </summary>
public sealed class ConcurrencyConventionTests : ConcurrencyConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
