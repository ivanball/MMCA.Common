using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Layering;

/// <summary>
/// The cloud-vendor boundary, asserted against the framework's own core packages: every layer in
/// <see cref="CommonArchitectureMap"/> plus the service defaults (<c>MMCA.Common.Aspire</c>) and the
/// test infrastructure packages, which are the four places the Azure dependencies used to live. The
/// compile-time half, covering every core project's package references, is
/// <c>EnforceCloudVendorBoundary</c>.
/// <para>
/// One allowance: <c>MMCA.Common.Infrastructure</c> may name <c>Microsoft.Azure.Cosmos</c>, because
/// Cosmos DB is one of the four EF engines (<c>CosmosDbContext</c> picks its connection mode), not a
/// cloud service a host opts into.
/// </para>
/// </summary>
public sealed class CloudVendorIsolationTests : CloudVendorIsolationTestsBase
{
    protected override IEnumerable<Assembly> CloudNeutralAssemblies =>
    [
        .. new CommonArchitectureMap().Layers.Select(layer => layer.Assembly),
        typeof(Aspire.Extensions).Assembly,
        typeof(Testing.Fixtures.CrossServiceFixtureBase).Assembly,
        typeof(Testing.Aspire.Fixtures.AppHostFixtureBase).Assembly,
        typeof(CloudVendorIsolationTestsBase).Assembly,
    ];

    protected override IReadOnlyDictionary<string, IReadOnlyCollection<string>> AllowedVendorPrefixes { get; } =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["MMCA.Common.Infrastructure"] = ["Microsoft.Azure.Cosmos"],
        };
}
