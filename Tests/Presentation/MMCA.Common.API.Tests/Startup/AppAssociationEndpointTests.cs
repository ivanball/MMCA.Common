using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.API.Startup;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// In-memory TestServer tests over the two well-known app-association documents mapped by
/// <see cref="AppAssociationEndpointExtensions"/> (ADR-043): Android Digital Asset Links at
/// <c>/.well-known/assetlinks.json</c> (relation + android_app target with the signing-cert
/// fingerprints) and the Apple App Site Association document at its exact extensionless path
/// (<c>applinks</c> details/components built from <c>AppleAppLinkComponents</c> plus the
/// <c>webcredentials</c> envelope). Both endpoints must carry anonymous metadata (the OS and
/// Apple's CDN fetch them without credentials) and stay out of the OpenAPI description.
/// </summary>
public sealed class AppAssociationEndpointTests
{
    private static AppAssociationOptions CreateOptions() => new()
    {
        AndroidPackageName = "com.example.adc",
        AndroidCertFingerprints =
        [
            "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99",
            "11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00",
        ],
        AppleAppId = "TEAMID123.com.example.adc",
        AppleAppLinkComponents = ["/conference/*", "/sessions/*"],
    };

    // ── Android Digital Asset Links ──
    [Fact]
    public async Task GetAssetLinks_PublishesTheHandleAllUrlsRelationForTheAndroidTarget()
    {
        using var host = await CreateHostAsync(CreateOptions());
        using HttpClient client = host.GetTestClient();

        using var response = await client.GetAsync(
            new Uri(AppAssociationEndpointExtensions.AssetLinksPath, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetArrayLength().Should().Be(1, "the document is a single-statement array");

        var statement = document.RootElement[0];
        statement.GetProperty("relation").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("delegate_permission/common.handle_all_urls");

        var target = statement.GetProperty("target");
        target.GetProperty("namespace").GetString().Should().Be("android_app");
        target.GetProperty("package_name").GetString().Should().Be("com.example.adc");
        target.GetProperty("sha256_cert_fingerprints").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(
                "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99",
                "11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00");
    }

    // ── Apple App Site Association ──
    [Fact]
    public async Task GetAppleAppSiteAssociation_AtTheExactExtensionlessPath_PublishesApplinksAndWebcredentials()
    {
        // Apple requires this exact path with NO file extension; the content type must still be JSON.
        AppAssociationEndpointExtensions.AppleAppSiteAssociationPath
            .Should().Be("/.well-known/apple-app-site-association");

        using var host = await CreateHostAsync(CreateOptions());
        using HttpClient client = host.GetTestClient();

        using var response = await client.GetAsync(
            new Uri(AppAssociationEndpointExtensions.AppleAppSiteAssociationPath, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var details = root.GetProperty("applinks").GetProperty("details");
        details.GetArrayLength().Should().Be(1);
        details[0].GetProperty("appIDs").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("TEAMID123.com.example.adc");

        // Each configured URL pattern becomes a { "/": pattern } component, in order.
        var components = details[0].GetProperty("components");
        components.GetArrayLength().Should().Be(2);
        components[0].GetProperty("/").GetString().Should().Be("/conference/*");
        components[1].GetProperty("/").GetString().Should().Be("/sessions/*");

        root.GetProperty("webcredentials").GetProperty("apps").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("TEAMID123.com.example.adc");
    }

    // ── Defaults: fingerprints/components not configured ──
    [Fact]
    public async Task BothDocuments_KeepValidEmptyArrays_WhenFingerprintsAndComponentsAreNotConfigured()
    {
        var options = new AppAssociationOptions
        {
            AndroidPackageName = "com.example.adc",
            AppleAppId = "TEAMID123.com.example.adc",
        };
        using var host = await CreateHostAsync(options);
        using HttpClient client = host.GetTestClient();

        using var assetLinks = await client.GetAsync(
            new Uri(AppAssociationEndpointExtensions.AssetLinksPath, UriKind.Relative));
        using var assetLinksDocument = JsonDocument.Parse(await assetLinks.Content.ReadAsStringAsync());
        assetLinksDocument.RootElement[0].GetProperty("target")
            .GetProperty("sha256_cert_fingerprints").GetArrayLength().Should().Be(0);

        using var aasa = await client.GetAsync(
            new Uri(AppAssociationEndpointExtensions.AppleAppSiteAssociationPath, UriKind.Relative));
        using var aasaDocument = JsonDocument.Parse(await aasa.Content.ReadAsStringAsync());
        aasaDocument.RootElement.GetProperty("applinks").GetProperty("details")[0]
            .GetProperty("components").GetArrayLength().Should().Be(0);
    }

    // ── Endpoint metadata: anonymous + hidden from OpenAPI ──
    [Fact]
    public async Task BothEndpoints_AreAnonymousAndExcludedFromTheApiDescription()
    {
        ICollection<EndpointDataSource>? dataSources = null;
        using var host = await CreateHostAsync(CreateOptions(), endpoints => dataSources = endpoints.DataSources);

        dataSources.Should().NotBeNull();
        var endpoints = dataSources!.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToList();
        endpoints.Select(e => e.RoutePattern.RawText).Should().BeEquivalentTo(
            AppAssociationEndpointExtensions.AssetLinksPath,
            AppAssociationEndpointExtensions.AppleAppSiteAssociationPath);

        foreach (RouteEndpoint endpoint in endpoints)
        {
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull(
                $"{endpoint.RoutePattern.RawText} is fetched without credentials by the OS and Apple's CDN");

            var exclude = endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>();
            exclude.Should().NotBeNull($"{endpoint.RoutePattern.RawText} is OS plumbing, not API surface");
            exclude!.ExcludeFromDescription.Should().BeTrue();
        }
    }

    // ── Options guard ──
    [Fact]
    public async Task MapAppAssociationEndpoints_WithNullOptions_Throws()
    {
        Func<Task> act = () => CreateHostAsync(options: null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Helpers ──
    private static async Task<IHost> CreateHostAsync(
        AppAssociationOptions options,
        Action<IEndpointRouteBuilder>? inspect = null) =>
        await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .UseEnvironment(Environments.Production)
                .ConfigureServices(services => services.AddRouting())
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapAppAssociationEndpoints(options);
                        inspect?.Invoke(endpoints);
                    });
                }))
            .StartAsync();
}
