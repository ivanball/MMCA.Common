using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace MMCA.Common.Testing;

/// <summary>
/// Contract guard (rubric §9) for a service host's OpenAPI document. Each extracted service host
/// serves <c>/openapi/v1.json</c> outside Production (wired in its <c>Program.cs</c> via
/// <c>MapCommonOpenApi()</c>). This boots the real host in-process and asserts the document is
/// served, is a well-formed OpenAPI 3.x document that describes the API surface, and still exposes
/// the core public resources, so an accidental controller/route removal, or a regression that stops
/// emitting the document, fails CI instead of silently changing the published contract. There is no
/// committed snapshot file: the assertions run against the live document, so new controllers can
/// never leave a stale snapshot behind. Authored once here and re-run as a thin subclass per host:
/// the subclass supplies its fixture, its <see cref="MinimumPathCount"/> floor, and the
/// <see cref="CorePublicResources"/> its contract must keep describing.
/// </summary>
/// <typeparam name="TFixture">The concrete fixture type implementing <see cref="IIntegrationTestFixture"/>.</typeparam>
public abstract class OpenApiContractTestsBase<TFixture> : IntegrationTestBase<TFixture>
    where TFixture : IIntegrationTestFixture
{
    protected OpenApiContractTestsBase(TFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>The path the host serves its OpenAPI document at.</summary>
    protected virtual string OpenApiDocumentPath => "/openapi/v1.json";

    /// <summary>
    /// Minimum number of entries the document's <c>paths</c> object must contain: a coarse floor
    /// under the host's route surface, so a regression that drops a controller wholesale is caught
    /// even when the pinned <see cref="CorePublicResources"/> survive.
    /// </summary>
    protected abstract int MinimumPathCount { get; }

    /// <summary>
    /// The because-reason attached to the <see cref="MinimumPathCount"/> assertion. Override with the
    /// host's route inventory (e.g. "the Catalog service exposes its Categories/Products routes") so
    /// a failure message explains what the floor stands for.
    /// </summary>
    protected virtual string MinimumPathCountBecause => "the service must keep describing its controller route surface";

    /// <summary>
    /// Resource paths (e.g. <c>"/Events"</c>, <c>"/Orders"</c>) the document must keep describing.
    /// Presence (not exact casing) is the contract: a removed or renamed public resource must fail.
    /// </summary>
    protected abstract IReadOnlyList<string> CorePublicResources { get; }

    [Fact]
    public async Task OpenApiDocument_IsServed_AsWellFormedOpenApiDescribingTheApiSurface()
    {
        using var doc = JsonDocument.Parse(await GetOpenApiJsonAsync().ConfigureAwait(false));

        doc.RootElement.GetProperty("openapi").GetString()
            .Should().StartWith("3.", "the document must be OpenAPI 3.x");
        doc.RootElement.GetProperty("info").GetProperty("title").GetString()
            .Should().NotBeNullOrWhiteSpace();
        doc.RootElement.TryGetProperty("paths", out var paths)
            .Should().BeTrue("the document must contain a paths object");
        paths.EnumerateObject().Count()
            .Should().BeGreaterThanOrEqualTo(MinimumPathCount, MinimumPathCountBecause);
    }

    [Fact]
    public async Task OpenApiDocument_DescribesEveryCorePublicResource()
    {
        CorePublicResources.Should().NotBeEmpty(
            "the subclass must pin at least one core public resource path (otherwise this guard passes vacuously)");

        using var doc = JsonDocument.Parse(await GetOpenApiJsonAsync().ConfigureAwait(false));
        var paths = doc.RootElement.GetProperty("paths");

        // Presence (not exact casing) is the contract: a removed or renamed public resource must fail here.
        var missing = CorePublicResources
            .Where(expected => !paths.EnumerateObject()
                .Any(p => string.Equals(p.Name, expected, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        missing.Should().BeEmpty(
            "the OpenAPI contract should still describe every pinned public resource; missing: {0}",
            string.Join(", ", missing));
    }

    /// <summary>
    /// Fetches the OpenAPI document (anonymously: the document itself is unauthenticated outside
    /// Production) and asserts it is served, returning the raw JSON.
    /// </summary>
    protected async Task<string> GetOpenApiJsonAsync()
    {
        ClearAuthentication();
        using var response = await Client.GetAsync(OpenApiDocumentPath).ConfigureAwait(false);
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the service must serve its OpenAPI document at {0} outside Production",
            OpenApiDocumentPath);
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }
}
