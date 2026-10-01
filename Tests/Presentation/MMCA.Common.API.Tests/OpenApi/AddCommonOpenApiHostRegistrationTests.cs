using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Asp.Versioning;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Startup;
using MMCA.Common.API.Startup.Endpoints;

namespace MMCA.Common.API.Tests.OpenApi;

/// <summary>
/// <c>AddCommonOpenApi()</c> configures the document the host registers with ASP.NET Core's own
/// <c>AddOpenApi()</c>; it never registers a document itself. The host call has to stay in the host:
/// the OpenAPI XML-comment source generator attaches the host's controller summaries by intercepting
/// the <c>AddOpenApi</c> call sites of the project being compiled, so a registration made inside this
/// framework assembly carried none of them. The second registration it used to make (the
/// API-versioning document builder) also rewrote <c>info.title</c> and <c>info.version</c>, added a
/// value list to every <c>api-version</c> parameter and emitted one extra document per discovered
/// API version. A host swapping its hand-written registration for the framework pair must therefore
/// get exactly the document plain <c>AddOpenApi()</c> produces.
/// <para>
/// This test project plays the host: its <c>AddOpenApi()</c> calls are intercepted by the generator
/// (the project enables the generator's interceptor namespace), so the summary of
/// <see cref="HostRegistrationProbeController"/> is observable exactly as a service host's would be.
/// </para>
/// </summary>
public sealed class AddCommonOpenApiHostRegistrationTests
{
    [Fact]
    public async Task HostRegistrationThroughTheFrameworkPair_ProducesThePlainAddOpenApiDocument()
    {
        JsonNode plain = await FetchDocumentAsync(withFrameworkPair: false);
        JsonNode framework = await FetchDocumentAsync(withFrameworkPair: true);

        framework.ToJsonString().Should().Be(
            plain.ToJsonString(),
            "AddCommonOpenApi only configures the host's document and is inert without strongly typed identifiers");
    }

    [Fact]
    public async Task HostRegistrationThroughTheFrameworkPair_KeepsThePlainTitleVersionAndParameterShape()
    {
        JsonNode document = await FetchDocumentAsync(withFrameworkPair: true);

        document["info"]!["title"]!.GetValue<string>().Should().Be(
            typeof(AddCommonOpenApiHostRegistrationTests).Assembly.GetName().Name + " | v1",
            "the title is the host application's name, not the API-versioning description");
        document["info"]!["version"]!.GetValue<string>().Should().Be("1.0.0");

        JsonNode apiVersion = document["paths"]!["/host-registration-probe/{id}"]!["get"]!["parameters"]!
            .AsArray()
            .Single(parameter => parameter!["name"]!.GetValue<string>() == "api-version")!;
        apiVersion["schema"]!.AsObject().ContainsKey("enum").Should().BeFalse(
            "the api-version header is described as the plain string plain AddOpenApi() emits");
    }

    [Fact]
    public async Task HostRegistrationThroughTheFrameworkPair_KeepsTheHostAssemblysXmlSummaries()
    {
        JsonNode document = await FetchDocumentAsync(withFrameworkPair: true);

        document["paths"]!["/host-registration-probe/{id}"]!["get"]!["summary"]!.GetValue<string>().Should().Be(
            "Returns the supplied identifier.",
            "the XML-comment generator attaches the host's summaries through the host's own AddOpenApi() call");
    }

    [Fact]
    public async Task MapCommonOpenApi_WithoutTheHostsAddOpenApi_FailsInsteadOfServingNoDocument()
    {
        var builder = CreateBuilder(Environments.Development);
        builder.Services.AddCommonApiVersioning();
        builder.Services.AddCommonOpenApi();
        await using WebApplication app = builder.Build();

        Action map = () => app.MapCommonOpenApi();

        map.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddOpenApi()*AddCommonOpenApi()*");
    }

    private static async Task<JsonNode> FetchDocumentAsync(bool withFrameworkPair)
    {
        var builder = CreateBuilder(Environments.Development);
        builder.Services.AddCommonApiVersioning();
        builder.Services.AddOpenApi();
        if (withFrameworkPair)
        {
            builder.Services.AddCommonOpenApi();
        }

        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            for (int i = manager.FeatureProviders.Count - 1; i >= 0; i--)
            {
                if (manager.FeatureProviders[i] is IApplicationFeatureProvider<ControllerFeature>)
                {
                    manager.FeatureProviders.RemoveAt(i);
                }
            }

            manager.FeatureProviders.Add(new HostRegistrationProbeFeatureProvider());
        });

        await using WebApplication app = builder.Build();
        app.MapControllers();
        if (withFrameworkPair)
        {
            app.MapCommonOpenApi();
        }
        else
        {
            app.MapOpenApi().AllowAnonymous();
        }

        await app.StartAsync(TestContext.Current.CancellationToken);
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/openapi/v1.json", UriKind.Relative),
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        JsonNode document = JsonNode.Parse(json)!;
        document.AsObject().Remove("servers");
        return document;
    }

    private static WebApplicationBuilder CreateBuilder(string environmentName)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName,
        });

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers();
        return builder;
    }

    private sealed class HostRegistrationProbeFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) =>
            feature.Controllers.Add(typeof(HostRegistrationProbeController).GetTypeInfo());
    }
}

/// <summary>
/// Header-versioned probe controller, the shape every first-party service host uses.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("host-registration-probe")]
public sealed class HostRegistrationProbeController : ControllerBase
{
    /// <summary>Returns the supplied identifier.</summary>
    /// <param name="id">The identifier to return.</param>
    [HttpGet("{id}")]
    [ProducesResponseType<int>(StatusCodes.Status200OK)]
    public IActionResult Get(int id) => Ok(id);
}
