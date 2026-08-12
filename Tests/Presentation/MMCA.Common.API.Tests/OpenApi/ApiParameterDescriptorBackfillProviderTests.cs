using System.Net;
using System.Reflection;
using System.Text.Json;
using Asp.Versioning;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.OpenApi;
using MMCA.Common.API.Startup;

namespace MMCA.Common.API.Tests.OpenApi;

/// <summary>
/// Covers <see cref="ApiParameterDescriptorBackfillProvider"/>, the guard that keeps an unbound
/// route token (most importantly the <c>v{version:apiVersion}</c> segment that
/// <c>AddCommonApiVersioning</c> enables through <c>SubstituteApiVersionInUrl</c>) from failing
/// OpenAPI document generation.
/// <para>
/// The headline test is end to end on purpose. <c>Asp.Versioning.OpenApi</c> 10.2.1 dereferences
/// <c>ApiParameterDescription.ParameterDescriptor</c> without a null check inside
/// <c>XmlCommentsTransformer</c>, and that transformer is only attached when an XML documentation
/// file is found, so a mocked pipeline would assert nothing about the real failure. Booting a
/// TestServer over the real MVC + API versioning + OpenAPI stack and fetching the document is the
/// only assertion that actually reproduces the <c>500</c> when the guard is removed.
/// </para>
/// </summary>
public sealed class ApiParameterDescriptorBackfillProviderTests
{
    /// <summary>Route tokens declared by the probe controllers with no matching action parameter.</summary>
    private static readonly HashSet<string> UnboundTokenNames =
        new(StringComparer.Ordinal) { "version", "tenant" };

    // ── The regression: a URL-segment-versioned route must not fail the document ──
    [Fact]
    public async Task GetOpenApiDocument_WithUrlSegmentVersionedRoute_ReturnsTheDocument()
    {
        await using WebApplication app = await CreateHostAsync();
        using HttpClient client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "Asp.Versioning.OpenApi 10.2.1 throws a NullReferenceException on the null ParameterDescriptor "
            + "of an unbound route token, which surfaces as a 500 on the document endpoint");

        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var paths = document.RootElement.GetProperty("paths");

        paths.TryGetProperty("/api/v{version}/backfill-probe/{id}", out _).Should().BeTrue(
            "the segment-versioned route must still be described, not merely tolerated");
        paths.TryGetProperty("/api/backfill-probe/{tenant}/items", out _).Should().BeTrue(
            "an unbound route token that is not the API version fails identically and is guarded too");
    }

    // ── The invariant the upstream transformer relies on, asserted against the real pipeline ──
    [Fact]
    public async Task ApiDescriptions_ForUnboundRouteTokens_AlwaysCarryAParameterDescriptor()
    {
        await using WebApplication app = await CreateHostAsync();

        var descriptions = app.Services
            .GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .ToList();

        descriptions.Should().NotBeEmpty("the probe controllers must have been discovered");

        var unbound = descriptions
            .SelectMany(description => description.ParameterDescriptions)
            .Where(parameter => UnboundTokenNames.Contains(parameter.Name))
            .ToList();

        unbound.Should().NotBeEmpty("both probe routes declare a token with no matching action parameter");
        unbound.Should().AllSatisfy(parameter => parameter.ParameterDescriptor.Should().NotBeNull());
    }

    // ── Only the gaps are filled; a real descriptor is never overwritten ──
    [Fact]
    public void OnProvidersExecuted_FillsOnlyTheMissingDescriptors()
    {
        var existing = new ParameterDescriptor { Name = "kept", ParameterType = typeof(Guid) };
        var context = new ApiDescriptionProviderContext([]);
        var description = new ApiDescription();
        description.ParameterDescriptions.Add(new ApiParameterDescription
        {
            Name = "version",
            Type = typeof(string),
            Source = BindingSource.Path,
        });
        description.ParameterDescriptions.Add(new ApiParameterDescription
        {
            Name = "kept",
            Type = typeof(Guid),
            Source = BindingSource.Path,
            ParameterDescriptor = existing,
        });
        context.Results.Add(description);

        new ApiParameterDescriptorBackfillProvider().OnProvidersExecuted(context);

        var filled = description.ParameterDescriptions[0].ParameterDescriptor;
        filled.Should().NotBeNull();
        filled!.Name.Should().Be("version");
        filled.ParameterType.Should().Be<string>();

        description.ParameterDescriptions[1].ParameterDescriptor.Should().BeSameAs(
            existing,
            "a descriptor MVC or API versioning already supplied is richer than any reconstruction");
    }

    // ── Runs last so it observes every parameter other providers contributed ──
    // OnProvidersExecuted runs in DESCENDING order, so the lowest order executes last.
    [Fact]
    public void Order_IsLowest_SoTheGuardRunsAfterEveryOtherProvider() =>
        new ApiParameterDescriptorBackfillProvider().Order.Should().Be(int.MinValue);

    // ── Registered once even when a host calls both helpers ──
    [Fact]
    public void BothRegistrationHelpers_RegisterTheGuardExactlyOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddCommonApiVersioning();
        services.AddCommonOpenApi();

        services.Count(descriptor =>
                descriptor.ServiceType == typeof(IApiDescriptionProvider)
                && descriptor.ImplementationType == typeof(ApiParameterDescriptorBackfillProvider))
            .Should().Be(1, "TryAddEnumerable de-duplicates on the implementation type");
    }

    // ── Helpers ──
    private static async Task<WebApplication> CreateHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // Restrict discovery to this file's probe controllers so the assertions cannot be
        // perturbed by controllers belonging to other tests or to MMCA.Common.API itself.
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            for (int i = manager.FeatureProviders.Count - 1; i >= 0; i--)
            {
                if (manager.FeatureProviders[i] is IApplicationFeatureProvider<ControllerFeature>)
                {
                    manager.FeatureProviders.RemoveAt(i);
                }
            }

            manager.FeatureProviders.Add(new ProbeControllerFeatureProvider());
        });

        builder.Services.AddCommonApiVersioning();
        builder.Services.AddCommonOpenApi();

        WebApplication app = builder.Build();
        app.MapControllers();
        app.MapCommonOpenApi();
        await app.StartAsync();

        return app;
    }

    private sealed class ProbeControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Add(typeof(SegmentVersionedProbeController).GetTypeInfo());
            feature.Controllers.Add(typeof(UnboundRouteTokenProbeController).GetTypeInfo());
        }
    }
}

/// <summary>
/// Probe controller whose route carries the API version as a URL segment. Nothing binds
/// <c>version</c> to an action parameter, so MVC describes it with a null
/// <c>ParameterDescriptor</c>: the exact shape that fails document generation unguarded.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/backfill-probe")]
public sealed class SegmentVersionedProbeController : ControllerBase
{
    /// <summary>Echoes the supplied identifier.</summary>
    /// <param name="id">The identifier to echo.</param>
    [HttpGet("{id}")]
    public IActionResult Get(int id) => Ok(id);
}

/// <summary>
/// Probe controller with an unbound route token that is NOT the API version, proving the guard
/// addresses the general MVC condition rather than special-casing API versioning.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/backfill-probe/{tenant}/items")]
public sealed class UnboundRouteTokenProbeController : ControllerBase
{
    /// <summary>Returns a constant payload.</summary>
    [HttpGet]
    public IActionResult Get() => Ok("ok");
}
