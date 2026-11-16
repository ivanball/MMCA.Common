using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Aspire.Hosting.Tests.Topology;

/// <summary>
/// Unit tests for the front-door helpers <c>WithMmcaGateway</c>, <c>WithMmcaUi</c> and
/// <c>WithGatewayApiEndpoints</c>. Asserted on the application MODEL: which resources the gateway and
/// the UI reference (the service-discovery environment variables they end up with) and wait for, in
/// what order, which health check gates them, that their endpoints are external, and which host port
/// the <c>https</c> endpoint is pinned to.
/// </summary>
public sealed class GatewayUiExtensionsTests
{
    private const int GatewayPort = 6001;

    private const int UiPort = 6002;

    [Fact]
    public void WithMmcaGateway_NullArguments_Throw()
    {
        var model = Arrange();
        IResourceBuilder<ProjectResource> nullGateway = null!;

        FluentActions.Invoking(() => nullGateway.WithMmcaGateway(GatewayPort, model.Identity))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => model.Gateway.WithMmcaGateway(GatewayPort, null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => model.Gateway.WithMmcaGateway(GatewayPort, model.Identity, null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task WithMmcaGateway_ReferencesEveryService()
    {
        var model = Arrange();

        model.Gateway.WithMmcaGateway(GatewayPort, model.Identity, model.Catalog);

        var environment = await EnvironmentOf(model.Builder, model.Gateway.Resource);
        environment.Keys.Should().Contain(key => key.StartsWith("services__identity__", StringComparison.Ordinal));
        environment.Keys.Should().Contain(key => key.StartsWith("services__catalog__", StringComparison.Ordinal));
    }

    [Fact]
    public void WithMmcaGateway_WaitsForEveryService_InTheOrderGiven()
    {
        var model = Arrange();

        model.Gateway.WithMmcaGateway(GatewayPort, model.Catalog, model.Identity);

        WaitNames(model.Gateway).Should().Equal("catalog", "identity");
    }

    [Fact]
    public void WithMmcaGateway_GatesOnLiveness_NotReadiness()
    {
        var model = Arrange();

        model.Gateway.WithMmcaGateway(GatewayPort, model.Identity);

        var check = HealthCheckKeys(model.Gateway).Should().ContainSingle().Subject;
        check.Should().Contain("/alive");
        check.Should().NotContain("/health/ready");
    }

    [Fact]
    public void WithMmcaGateway_PinsTheHttpsPort_AndMarksEndpointsExternal()
    {
        var model = Arrange();

        model.Gateway.WithMmcaGateway(GatewayPort, model.Identity);

        Endpoint(model.Gateway, "https").Port.Should().Be(GatewayPort);
        model.Gateway.Resource.Annotations.OfType<EndpointAnnotation>().Should().OnlyContain(static e => e.IsExternal);
    }

    [Fact]
    public void WithMmcaGateway_WithNoServices_StillProbesAndPins()
    {
        var model = Arrange();

        model.Gateway.WithMmcaGateway(GatewayPort);

        WaitNames(model.Gateway).Should().BeEmpty();
        HealthCheckKeys(model.Gateway).Should().ContainSingle();
        Endpoint(model.Gateway, "https").Port.Should().Be(GatewayPort);
    }

    [Fact]
    public void WithMmcaGateway_ReturnsTheSameBuilder()
    {
        var model = Arrange();

        model.Gateway.WithMmcaGateway(GatewayPort, model.Identity).Should().BeSameAs(model.Gateway);
    }

    [Fact]
    public void WithMmcaUi_NullArguments_Throw()
    {
        var model = Arrange();
        IResourceBuilder<ProjectResource> nullUi = null!;

        FluentActions.Invoking(() => nullUi.WithMmcaUi(model.Gateway, UiPort, model.Identity))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => model.Ui.WithMmcaUi(null!, UiPort, model.Identity))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => model.Ui.WithMmcaUi(model.Gateway, UiPort, null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => model.Ui.WithMmcaUi(model.Gateway, UiPort, model.Identity, null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task WithMmcaUi_ReferencesTheGatewayAndEveryService()
    {
        var model = Arrange();

        model.Ui.WithMmcaUi(model.Gateway, UiPort, model.Identity, model.Catalog);

        var environment = await EnvironmentOf(model.Builder, model.Ui.Resource);
        environment.Keys.Should().Contain(key => key.StartsWith("services__gateway__", StringComparison.Ordinal));
        environment.Keys.Should().Contain(key => key.StartsWith("services__identity__", StringComparison.Ordinal));
        environment.Keys.Should().Contain(key => key.StartsWith("services__catalog__", StringComparison.Ordinal));
    }

    [Fact]
    public void WithMmcaUi_WaitsForTheGatewayFirst_ThenEveryServiceInTheOrderGiven()
    {
        var model = Arrange();

        model.Ui.WithMmcaUi(model.Gateway, UiPort, model.Catalog, model.Identity);

        WaitNames(model.Ui).Should().Equal("gateway", "catalog", "identity");
    }

    [Fact]
    public void WithMmcaUi_GatesOnReadiness()
    {
        var model = Arrange();

        model.Ui.WithMmcaUi(model.Gateway, UiPort, model.Identity);

        HealthCheckKeys(model.Ui).Should().ContainSingle()
            .Which.Should().Contain("/health/ready");
    }

    [Fact]
    public void WithMmcaUi_PinsTheHttpsPort_AndMarksEndpointsExternal()
    {
        var model = Arrange();

        model.Ui.WithMmcaUi(model.Gateway, UiPort, model.Identity);

        Endpoint(model.Ui, "https").Port.Should().Be(UiPort);
        model.Ui.Resource.Annotations.OfType<EndpointAnnotation>().Should().OnlyContain(static e => e.IsExternal);
    }

    [Fact]
    public void WithMmcaUi_ReturnsTheSameBuilder()
    {
        var model = Arrange();

        model.Ui.WithMmcaUi(model.Gateway, UiPort, model.Identity).Should().BeSameAs(model.Ui);
    }

    [Fact]
    public void WithGatewayApiEndpoints_NullArguments_Throw()
    {
        var model = Arrange();
        IResourceBuilder<ProjectResource> nullUi = null!;

        FluentActions.Invoking(() => nullUi.WithGatewayApiEndpoints(model.Gateway))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => model.Ui.WithGatewayApiEndpoints(null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task WithGatewayApiEndpoints_PointsBothApiEndpointsAtTheGatewayHttpsEndpoint()
    {
        var model = Arrange();

        model.Ui.WithGatewayApiEndpoints(model.Gateway);

        var environment = await EnvironmentOf(model.Builder, model.Ui.Resource);

        // Publish mode keeps the endpoint as its manifest expression, which names the resource and
        // the endpoint, so the assertion pins both rather than a port DCP happened to allocate.
        const string gatewayHttps = "{gateway.bindings.https.url}";
        environment.Should().ContainKey("Api__ApiEndpoint").WhoseValue.Should().Be(gatewayHttps);
        environment.Should().ContainKey("Api__WasmApiEndpoint").WhoseValue.Should().Be(gatewayHttps);
    }

    [Fact]
    public void WithGatewayApiEndpoints_AddsNoWait()
    {
        var model = Arrange();

        model.Ui.WithGatewayApiEndpoints(model.Gateway).Should().BeSameAs(model.Ui);

        WaitNames(model.Ui).Should().BeEmpty();
    }

    // AddResource rather than AddProject: the extensions only need ProjectResources in the model, and
    // AddProject validates that the project file exists on disk. The gateway and the UI get the http
    // and https endpoints a launch profile would give them; the services get http only.
    private static Model Arrange()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var gateway = builder.AddResource(new ProjectResource("gateway"))
            .WithHttpEndpoint(name: "http")
            .WithHttpsEndpoint(name: "https");
        var ui = builder.AddResource(new ProjectResource("ui"))
            .WithHttpEndpoint(name: "http")
            .WithHttpsEndpoint(name: "https");
        var identity = builder.AddResource(new ProjectResource("identity")).WithHttpEndpoint(name: "http");
        var catalog = builder.AddResource(new ProjectResource("catalog")).WithHttpEndpoint(name: "http");

        return new Model(builder, gateway, ui, identity, catalog);
    }

    private static IReadOnlyList<string> WaitNames(IResourceBuilder<ProjectResource> resource) =>
        [.. resource.Resource.Annotations.OfType<WaitAnnotation>().Select(static w => w.Resource.Name)];

    private static IReadOnlyList<string> HealthCheckKeys(IResourceBuilder<ProjectResource> resource) =>
        [.. resource.Resource.Annotations.OfType<HealthCheckAnnotation>().Select(static a => a.Key)];

    private static EndpointAnnotation Endpoint(IResourceBuilder<ProjectResource> resource, string name) =>
        resource.Resource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == name);

    /// <summary>
    /// Resolves a resource's environment variables in publish mode, where every reference stays a
    /// manifest expression rather than a value DCP happened to allocate.
    /// </summary>
    private static async Task<Dictionary<string, string>> EnvironmentOf(
        IDistributedApplicationBuilder builder,
        IResource resource)
    {
        await using ServiceProvider services = builder.Services.BuildServiceProvider();
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Publish)
            {
                Services = services,
            });

        IExecutionConfigurationResult result = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext);

        result.Exception.Should().BeNull();
        return result.EnvironmentVariables.ToDictionary(StringComparer.Ordinal);
    }

    private sealed record Model(
        IDistributedApplicationBuilder Builder,
        IResourceBuilder<ProjectResource> Gateway,
        IResourceBuilder<ProjectResource> Ui,
        IResourceBuilder<ProjectResource> Identity,
        IResourceBuilder<ProjectResource> Catalog);
}
