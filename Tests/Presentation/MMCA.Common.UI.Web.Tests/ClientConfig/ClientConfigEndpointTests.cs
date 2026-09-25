using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Web.ClientConfig;

namespace MMCA.Common.UI.Web.Tests.ClientConfig;

/// <summary>
/// Tests for <c>MapClientConfigEndpoint</c>: the served document (the framework <c>api</c> section
/// plus a host's extras, camelCased as an anonymous-object payload would be), the fail-closed path
/// when <c>Api:WasmApiEndpoint</c> is missing (never a fallback to the service-discovery name), and
/// the anonymous, undocumented endpoint metadata the WASM boot depends on.
/// </summary>
public sealed class ClientConfigEndpointTests
{
    private const string Gateway = "https://gateway.example.com";

    [Fact]
    public async Task ServesTheWasmApiEndpoint()
    {
        await using var app = CreateApp(Gateway);
        app.MapClientConfigEndpoint();

        using var document = await InvokeAsync(app);

        document.RootElement.GetProperty("api").GetProperty("apiEndpoint").GetString().Should().Be(Gateway);
        document.RootElement.EnumerateObject().Should().ContainSingle();
    }

    [Fact]
    public async Task ServesTheHostExtrasBesideTheApiSection()
    {
        await using var app = CreateApp(Gateway, ("Support:Email", "help@example.com"));
        app.MapClientConfigEndpoint(config => config
            .Add("OAuth", new { GoogleEnabled = true, GitHubEnabled = false })
            .Add("Support", new { Email = config.Configuration["Support:Email"] }));

        using var document = await InvokeAsync(app);

        var root = document.RootElement;
        root.GetProperty("api").GetProperty("apiEndpoint").GetString().Should().Be(Gateway);
        root.GetProperty("oAuth").GetProperty("googleEnabled").GetBoolean().Should().BeTrue();
        root.GetProperty("oAuth").GetProperty("gitHubEnabled").GetBoolean().Should().BeFalse();
        root.GetProperty("support").GetProperty("email").GetString().Should().Be("help@example.com");
    }

    [Fact]
    public async Task WithoutAWasmApiEndpoint_FailsClosedInsteadOfServingTheDiscoveryName()
    {
        await using var app = CreateApp(wasmApiEndpoint: null);
        app.MapClientConfigEndpoint();

        var act = () => InvokeAsync(app);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Api:WasmApiEndpoint");
    }

    [Fact]
    public async Task AnExtraNamedApi_IsRefused()
    {
        await using var app = CreateApp(Gateway);
        app.MapClientConfigEndpoint(config => config.Add("api", new { ApiEndpoint = "https://elsewhere" }));

        var act = () => InvokeAsync(app);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TheEndpoint_IsAnonymousAndExcludedFromTheApiDescription()
    {
        await using var app = CreateApp(Gateway);
        app.MapClientConfigEndpoint();

        var endpoint = FindEndpoint(app);

        endpoint.Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull();
        endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()!.ExcludeFromDescription.Should().BeTrue();
        endpoint.RoutePattern.RawText.Should().Be(ClientConfigEndpointExtensions.ClientConfigPath);
    }

    private static WebApplication CreateApp(string? wasmApiEndpoint, params (string Key, string Value)[] extra)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Api:ApiEndpoint"] = "https+http://gateway",
            ["Api:WasmApiEndpoint"] = wasmApiEndpoint,
        };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddOptions<ApiSettings>().Bind(builder.Configuration.GetSection(ApiSettings.SectionName));
        return builder.Build();
    }

    private static RouteEndpoint FindEndpoint(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => string.Equals(e.RoutePattern.RawText, ClientConfigEndpointExtensions.ClientConfigPath, StringComparison.Ordinal));

    private static async Task<JsonDocument> InvokeAsync(WebApplication app)
    {
        var endpoint = FindEndpoint(app);
        await using var scope = app.Services.CreateAsyncScope();
        await using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = body;

        await endpoint.RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Position = 0;
        return await JsonDocument.ParseAsync(body, cancellationToken: TestContext.Current.CancellationToken);
    }
}
