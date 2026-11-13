using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.API.Tests.OpenApi;

/// <summary>
/// What one probe controller exposes when hosted alone by <see cref="OpenApiProbeHost"/>: the raw
/// OpenAPI document text, the route templates that were mapped, and the ApiExplorer relative paths.
/// Lets an opt-in test prove an action is absent at all three layers, not just from the document.
/// </summary>
/// <param name="Document">The <c>/openapi/v1.json</c> body exactly as served.</param>
/// <param name="Routes">Every mapped route template's raw text.</param>
/// <param name="ApiPaths">Every ApiExplorer description's relative path.</param>
internal sealed record OpenApiProbeSurface(string Document, IReadOnlyList<string> Routes, IReadOnlyList<string> ApiPaths)
{
    /// <summary>Hosts <paramref name="controllerType"/> alone and captures its surface.</summary>
    /// <param name="controllerType">The probe controller.</param>
    /// <returns>The captured surface.</returns>
    public static async Task<OpenApiProbeSurface> CaptureAsync(Type controllerType)
    {
        await using WebApplication app = await OpenApiProbeHost.CreateAsync(controllerType);
        using HttpClient client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the document must generate before it can be compared");
        string document = await response.Content.ReadAsStringAsync();

        List<string> routes =
        [
            .. ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty),
        ];

        List<string> apiPaths =
        [
            .. app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
                .ApiDescriptionGroups.Items
                .SelectMany(group => group.Items)
                .Select(description => description.RelativePath ?? string.Empty),
        ];

        return new OpenApiProbeSurface(document, routes, apiPaths);
    }

    /// <summary>Whether any mapped route or ApiExplorer path ends with <paramref name="segment"/>.</summary>
    /// <param name="segment">The trailing route segment, for example <c>/cursor</c>.</param>
    /// <returns><see langword="true"/> when the action is exposed at either layer.</returns>
    public bool Exposes(string segment) =>
        Routes.Any(route => route.EndsWith(segment, StringComparison.OrdinalIgnoreCase))
        || ApiPaths.Any(path => path.EndsWith(segment, StringComparison.OrdinalIgnoreCase));
}
