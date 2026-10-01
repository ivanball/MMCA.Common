using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Startup;
using MMCA.Common.API.Startup.Endpoints;

namespace MMCA.Common.API.Tests.OpenApi;

/// <summary>
/// <c>MapCommonOpenApi()</c> declares its document anonymous. The framework registers a fallback
/// authorization policy (SEC-Common-16) that gates every endpoint stating nothing, and
/// <c>/openapi</c> is not one of its exempt prefixes, so an undeclared mapping answered 401 to the
/// contract-snapshot tests and API tooling. That is why every ADC and Store service host mapped
/// <c>app.MapOpenApi().AllowAnonymous()</c> by hand instead of calling the framework method. The
/// outside-Production gate is unchanged: Production still maps nothing.
/// </summary>
public sealed class MapCommonOpenApiAuthorizationTests
{
    [Fact]
    public async Task MapCommonOpenApi_OutsideProduction_DeclaresTheDocumentEndpointAnonymous()
    {
        await using WebApplication app = Build(Environments.Development);

        app.MapCommonOpenApi();

        RouteEndpoint endpoint = OpenApiEndpoints(app).Should().ContainSingle().Subject;
        endpoint.Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull(
            "the framework fallback policy would otherwise gate the contract document");
    }

    [Fact]
    public async Task MapCommonOpenApi_InProduction_StillMapsNothing()
    {
        await using WebApplication app = Build(Environments.Production);

        app.MapCommonOpenApi();

        OpenApiEndpoints(app).Should().BeEmpty();
    }

    [Fact]
    public async Task MapCommonOpenApi_UnderTheFrameworkFallbackPolicy_ServesTheDocumentToAnAnonymousCaller()
    {
        await using WebApplication app = Build(Environments.Development, withFallbackPolicy: true);
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCommonOpenApi();
        await app.StartAsync(TestContext.Current.CancellationToken);

        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/openapi/v1.json", UriKind.Relative),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    private static WebApplication Build(string environmentName, bool withFallbackPolicy = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName,
        });

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers();
        builder.Services.AddCommonApiVersioning();
        builder.Services.AddOpenApi();
        builder.Services.AddCommonOpenApi();

        if (withFallbackPolicy)
        {
            builder.Services.AddAuthentication();
            builder.Services.AddAuthorizationPolicies();
        }

        return builder.Build();
    }

    private static List<RouteEndpoint> OpenApiEndpoints(WebApplication app) =>
        [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.Contains("openapi", StringComparison.OrdinalIgnoreCase) == true)];
}
