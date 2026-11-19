using AwesomeAssertions;
using MMCA.Common.Testing.Conformance.ServiceDiscovery;
using Xunit;

namespace MMCA.Common.Testing.Tests.Conformance.ServiceDiscovery;

/// <summary>
/// Drives <see cref="AppHostBicepEdgeParser"/> with the ADC-shape and Store-shape samples and pins
/// the exact edge set each one yields.
/// </summary>
public sealed class AppHostBicepEdgeParserTests
{
    [Fact]
    public void AdcShapeAppHost_YieldsStatementReferencesAndFrontDoorArguments()
    {
        var edges = AppHostBicepEdgeParser.ParseAppHostEdges(ServiceDiscoverySamples.Source(ServiceDiscoverySamples.AdcShapeAppHostLines));

        edges.Should().BeEquivalentTo(
            ServiceDiscoverySamples.AdcShapeAppHostEdges,
            "a later-statement WithReference and every resource argument of WithMmcaGateway / WithMmcaUi are edges; "
            + "a commented-out call, a non-project reference (Redis) and WithJwksDiscovery are not");
    }

    [Fact]
    public void StoreShapeAppHost_YieldsChainedAndStatementReferences()
    {
        var edges = AppHostBicepEdgeParser.ParseAppHostEdges(ServiceDiscoverySamples.Source(ServiceDiscoverySamples.StoreShapeAppHostLines));

        edges.Should().BeEquivalentTo(
            ServiceDiscoverySamples.StoreShapeAppHostEdges,
            "a WithReference chained onto a multi-line AddDotnetProject declaration belongs to the declared resource");
    }

    [Fact]
    public void WithJwksDiscovery_IsNotAnEdge()
    {
        var source = ServiceDiscoverySamples.Source(
        [
            "var identityService = builder.AddDotnetProject(\"identity\", \"../Identity/Identity.csproj\");",
            "var gateway = builder.AddDotnetProject(\"gateway\", \"../Gateway/Gateway.csproj\");",
            "var salesService = builder.AddDotnetProject(\"sales\", \"../Sales/Sales.csproj\");",
            "salesService.WithJwksDiscovery(identityService, gateway);",
        ]);

        var edges = AppHostBicepEdgeParser.ParseAppHostEdges(source);

        edges.Should().BeEmpty("the JWKS wiring sets a token-validation address, not a service-discovery name");
    }

    [Fact]
    public void ObsoleteAddProjectDeclaration_IsNotAResource()
    {
        var source = ServiceDiscoverySamples.Source(
        [
            "var identityService = builder.AddProject<Projects.Identity>(\"identity\");",
            "var salesService = builder.AddProject<Projects.Sales>(\"sales\")",
            "    .WithReference(identityService);",
        ]);

        var edges = AppHostBicepEdgeParser.ParseAppHostEdges(source);

        edges.Should().BeEmpty("only AddDotnetProject declares a project resource; AddProject is obsolete in Aspire 17");
    }

    [Fact]
    public void ReferenceInsideAStringLiteral_SurvivesCommentStripping()
    {
        var source = ServiceDiscoverySamples.Source(
        [
            "var identityService = builder.AddDotnetProject(\"identity\", \"../Identity/Identity.csproj\");",
            "var salesService = builder.AddDotnetProject(\"sales\", \"../Sales/Sales.csproj\")",
            "    .WithEnvironment(\"Docs__Url\", \"https://example.test/a\").WithReference(identityService); // trailing note",
        ]);

        var edges = AppHostBicepEdgeParser.ParseAppHostEdges(source);

        edges.Should().BeEquivalentTo(["sales->identity"], "a // inside a string literal does not start a comment");
    }

    [Fact]
    public void AdcShapeBicep_AttributesEntriesToTheirOwnContainerAppOnly()
    {
        var edges = AppHostBicepEdgeParser.ParseBicepEdges(ServiceDiscoverySamples.AdcShapeBicepLines);

        edges.Should().BeEquivalentTo(
            ServiceDiscoverySamples.AdcShapeBicepEdges,
            "a services__ string inside a module block belongs to no container app");
    }

    [Fact]
    public void StoreShapeBicep_ReadsAppNameValuesAndSkipsOtherResources()
    {
        var edges = AppHostBicepEdgeParser.ParseBicepEdges(ServiceDiscoverySamples.StoreShapeBicepLines);

        edges.Should().BeEquivalentTo(
            ServiceDiscoverySamples.StoreShapeBicepEdges,
            "the peer is the env-var name segment, whatever the value expression, and a Key Vault secret is no container app");
    }
}
