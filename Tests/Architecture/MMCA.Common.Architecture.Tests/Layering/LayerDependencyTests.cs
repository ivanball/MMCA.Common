using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Layering;

/// <summary>
/// Clean Architecture layer-flow rules for the MMCA.Common framework packages, driven by the shared
/// rule library (<see cref="LayerDependencyTestsBase"/>) over <see cref="CommonArchitectureMap"/>.
/// <para>
/// Plus the runtime half of <c>EnforceServiceHostLayerBoundary</c>: the composed service host stays a
/// SERVICE host, so its compiled assembly references no UI layer, no gateway, no MCP endpoint, no test
/// infrastructure, no language-model package and no AppHost model, whether the edge arrives through a
/// declared reference or a transitive one. (The Azure.Startup half lives in
/// <c>AzureStartupPackageBoundaryTests</c>, which already asserts its Aspire-and-Shared allow-list.)
/// </para>
/// </summary>
public sealed class LayerDependencyTests : LayerDependencyTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    [Theory]
    [InlineData("MMCA.Common.UI")]
    [InlineData("MMCA.Common.Gateway")]
    [InlineData("MMCA.Common.Mcp")]
    [InlineData("MMCA.Common.Testing")]
    [InlineData("MMCA.Common.AI")]
    [InlineData("MMCA.Common.Aspire.Hosting")]
    public void ServiceHost_ReferencesNo_ForbiddenAssembly(string forbiddenSegment)
    {
        // Matched on the name segment, as the AI isolation rule does: "MMCA.Common.UI" covers
        // MMCA.Common.UI and MMCA.Common.UI.Web, but not a sibling that merely shares the prefix.
        var referenced = typeof(ServiceHost.ServiceHostBuilderExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(static name => name.Name ?? string.Empty)
            .ToList();

        referenced.Should().NotContain(
            name => string.Equals(name, forbiddenSegment, StringComparison.Ordinal)
                || name.StartsWith(forbiddenSegment + ".", StringComparison.Ordinal),
            "MMCA.Common.ServiceHost depends on API, Aspire service defaults and Grpc only, never {0}",
            forbiddenSegment);
    }

    [Fact]
    public void ServiceHost_ReferencesTheLayersItComposes()
    {
        // Guards the theory above against passing vacuously on an assembly that references nothing.
        var referenced = typeof(ServiceHost.ServiceHostBuilderExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(static name => name.Name)
            .ToList();

        referenced.Should().Contain(["MMCA.Common.API", "MMCA.Common.Aspire"]);
    }
}
