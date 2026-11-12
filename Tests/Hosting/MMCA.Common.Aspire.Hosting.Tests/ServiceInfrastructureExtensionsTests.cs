using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Aspire.Hosting.Tests;

/// <summary>
/// Unit tests for <c>WithMmcaServiceInfrastructure</c>, the one call that replaces the per-service
/// infrastructure chain every ADC and Store service repeats in its AppHost (data source, cache,
/// broker, the waits, the startup probe and the external endpoints). Asserted on the application
/// MODEL: which environment variables the service ends up with, which resources it waits for and in
/// what order, which health check gates it, and that its endpoints are external.
/// </summary>
public sealed class ServiceInfrastructureExtensionsTests
{
    private const string ServiceName = "svc";

    private const string DatabaseName = "svc-db";

    private const string CacheName = "redis";

    private const string LogicalName = "Svc";

    [Fact]
    public void NullArguments_Throw()
    {
        var model = Arrange();
        IResourceBuilder<ProjectResource> nullService = null!;

        FluentActions.Invoking(() => nullService.WithMmcaServiceInfrastructure(
                model.Database, LogicalName, model.Cache, model.MailServer, model.AttachBroker, ServiceEndpointProfile.Http2Only))
            .Should().Throw<ArgumentNullException>();

        FluentActions.Invoking(() => model.Service.WithMmcaServiceInfrastructure(
                null!, LogicalName, model.Cache, model.MailServer, model.AttachBroker, ServiceEndpointProfile.Http2Only))
            .Should().Throw<ArgumentNullException>();

        FluentActions.Invoking(() => model.Service.WithMmcaServiceInfrastructure(
                model.Database, LogicalName, null!, model.MailServer, model.AttachBroker, ServiceEndpointProfile.Http2Only))
            .Should().Throw<ArgumentNullException>();

        FluentActions.Invoking(() => model.Service.WithMmcaServiceInfrastructure(
                model.Database, LogicalName, model.Cache, null!, model.AttachBroker, ServiceEndpointProfile.Http2Only))
            .Should().Throw<ArgumentNullException>();

        FluentActions.Invoking(() => model.Service.WithMmcaServiceInfrastructure(
                model.Database, LogicalName, model.Cache, model.MailServer, null!, ServiceEndpointProfile.Http2Only))
            .Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankLogicalName_Throws(string logicalName)
    {
        var model = Arrange();

        var act = () => model.Service.WithMmcaServiceInfrastructure(
            model.Database, logicalName, model.Cache, model.MailServer, model.AttachBroker, ServiceEndpointProfile.Http2Only);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task SetsDataSourceConnectionStringKey()
    {
        var model = Arrange();

        Apply(model, ServiceEndpointProfile.Http2Only);

        var environment = await EnvironmentOf(model.Builder, model.Service.Resource);
        environment.Should().ContainKey("DataSources__Svc__SQLServerConnectionString");
    }

    [Fact]
    public async Task ReferencesTheCache()
    {
        var model = Arrange();

        Apply(model, ServiceEndpointProfile.Http2Only);

        var environment = await EnvironmentOf(model.Builder, model.Service.Resource);
        environment.Should().ContainKey("ConnectionStrings__" + CacheName);
    }

    [Fact]
    public async Task AttachesTheBroker()
    {
        var model = Arrange();

        Apply(model, ServiceEndpointProfile.Http2Only);

        var environment = await EnvironmentOf(model.Builder, model.Service.Resource);
        environment.Should().ContainKey("MessageBus__Provider")
            .WhoseValue.Should().Be("RabbitMq");
    }

    // Aspire's WaitFor on a child resource (the database) also adds a wait on its parent server, so
    // the assertion is the relative order of the four resources the call owns, not the full list.
    [Fact]
    public void WaitAnnotations_AreDatabaseBrokerCacheMail_InThatOrder()
    {
        var model = Arrange();

        Apply(model, ServiceEndpointProfile.Http2Only);

        string[] owned = [DatabaseName, Extensions.DefaultBrokerResourceName, CacheName, Extensions.DefaultMailDevResourceName];
        var waits = model.Service.Resource.Annotations.OfType<WaitAnnotation>()
            .Select(static w => w.Resource.Name)
            .Where(name => owned.Contains(name, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal);

        waits.Should().Equal(owned);
    }

    [Fact]
    public void Http2Only_RegistersTheH2cCheckOnHttp()
    {
        var model = Arrange();

        Apply(model, ServiceEndpointProfile.Http2Only);

        HealthCheckKeys(model.Service).Should().ContainSingle()
            .Which.Should().Be("svc-h2c-http");
    }

    [Fact]
    public void Http2Only_WithEndpointName_UsesIt()
    {
        var model = Arrange();

        Apply(model, ServiceEndpointProfile.Http2Only, probeEndpointName: "grpc");

        HealthCheckKeys(model.Service).Should().ContainSingle()
            .Which.Should().Be("svc-h2c-grpc");
    }

    // The mixed profile's default endpoint serves Http1AndHttp2, so Aspire's stock HTTP check is the
    // right probe there; the h2c check would be redundant. The stock check's key format is Aspire's,
    // so the assertion is only that exactly one check is associated and it is not an h2c one.
    [Fact]
    public void Mixed_RegistersOneStockHttpCheck_AndNoH2cCheck()
    {
        var model = Arrange();

        Apply(model, ServiceEndpointProfile.Mixed);

        HealthCheckKeys(model.Service).Should().ContainSingle()
            .Which.Should().NotContain("-h2c-");
    }

    [Fact]
    public void MarksEndpointsExternal()
    {
        var model = Arrange();

        Apply(model, ServiceEndpointProfile.Http2Only);

        var endpoints = model.Service.Resource.Annotations.OfType<EndpointAnnotation>().ToList();
        endpoints.Should().NotBeEmpty();
        endpoints.Should().OnlyContain(static e => e.IsExternal);
    }

    [Fact]
    public void ReturnsTheSameBuilder()
    {
        var http2Only = Arrange();
        var mixed = Arrange();

        Apply(http2Only, ServiceEndpointProfile.Http2Only).Should().BeSameAs(http2Only.Service);
        Apply(mixed, ServiceEndpointProfile.Mixed).Should().BeSameAs(mixed.Service);
    }

    private static IResourceBuilder<ProjectResource> Apply(
        Model model,
        ServiceEndpointProfile profile,
        string? probeEndpointName = null) =>
        model.Service.WithMmcaServiceInfrastructure(
            model.Database,
            LogicalName,
            model.Cache,
            model.MailServer,
            model.AttachBroker,
            profile,
            probeEndpointName);

    // AddResource rather than AddProject: the extension only needs a ProjectResource in the model,
    // and AddProject validates that the project file exists on disk. The connection-string resource
    // stands in for Redis, so this project needs no Redis hosting package.
    private static Model Arrange()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var service = builder.AddResource(new ProjectResource(ServiceName)).WithHttpEndpoint(name: "http");
        var database = builder.AddSqlServer("sql").AddDatabase(DatabaseName);
        var cache = builder.AddConnectionString(CacheName);
        var mailServer = builder.AddMailDev();
        var broker = builder.AddMessageBroker();

        return new Model(builder, service, database, cache, mailServer, s => s.WithBroker(broker));
    }

    private static IReadOnlyList<string> HealthCheckKeys(IResourceBuilder<ProjectResource> service) =>
        [.. service.Resource.Annotations.OfType<HealthCheckAnnotation>().Select(static a => a.Key)];

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
        IResourceBuilder<ProjectResource> Service,
        IResourceBuilder<SqlServerDatabaseResource> Database,
        IResourceBuilder<IResourceWithConnectionString> Cache,
        IResourceBuilder<IResource> MailServer,
        Func<IResourceBuilder<ProjectResource>, IResourceBuilder<ProjectResource>> AttachBroker);
}
