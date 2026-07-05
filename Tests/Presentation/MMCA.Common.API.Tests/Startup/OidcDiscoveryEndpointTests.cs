using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.API.Startup;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// In-memory TestServer tests over <c>GET /.well-known/openid-configuration</c> mapped by
/// <see cref="OidcDiscoveryEndpointExtensions"/>: 404 on non-Identity hosts (no
/// <c>Jwt:Issuer</c>), and on Identity hosts a minimal RFC 8414 document whose fields keep
/// their exact snake_case names, with <c>jwks_uri</c> derived from the issuer.
/// </summary>
public sealed class OidcDiscoveryEndpointTests
{
    // ── Non-Identity host: no issuer configured ──
    [Fact]
    public async Task GetDiscovery_WithoutIssuerConfigured_Returns404()
    {
        using var host = await CreateHostAsync(issuer: null);
        using HttpClient client = host.GetTestClient();

        using var response = await client.GetAsync(
            new Uri(OidcDiscoveryEndpointExtensions.DefaultOidcDiscoveryPath, UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "a host without Jwt:Issuer is not an Identity service and must not advertise discovery");
    }

    // ── Identity host: full document ──
    [Fact]
    public async Task GetDiscovery_WithIssuer_ReturnsSnakeCaseDocumentWithDerivedJwksUri()
    {
        const string issuer = "https://gateway.example.com:6001";
        using var host = await CreateHostAsync(issuer);
        using HttpClient client = host.GetTestClient();

        using var response = await client.GetAsync(
            new Uri(OidcDiscoveryEndpointExtensions.DefaultOidcDiscoveryPath, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        root.GetProperty("issuer").GetString().Should().Be(issuer);
        root.GetProperty("jwks_uri").GetString().Should().Be(
            $"{issuer}{JwksEndpointExtensions.DefaultJwksPath}",
            "jwks_uri must be derived from the issuer so both route through the same gateway");

        root.GetProperty("response_types_supported").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("token");
        root.GetProperty("subject_types_supported").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("public");
        root.GetProperty("id_token_signing_alg_values_supported").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("RS256");

        // The serializer must not camelCase the RFC field names: OIDC clients look up the
        // exact snake_case members, so a "jwksUri" spelling would break discovery.
        root.TryGetProperty("jwksUri", out _).Should().BeFalse();
        root.TryGetProperty("responseTypesSupported", out _).Should().BeFalse();
    }

    // ── Issuer normalization ──
    [Fact]
    public async Task GetDiscovery_WithTrailingSlashIssuer_TrimsSlashForJwksUriButEchoesIssuerVerbatim()
    {
        const string issuer = "https://gateway.example.com:6001/";
        using var host = await CreateHostAsync(issuer);
        using HttpClient client = host.GetTestClient();

        using var response = await client.GetAsync(
            new Uri(OidcDiscoveryEndpointExtensions.DefaultOidcDiscoveryPath, UriKind.Relative));

        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        root.GetProperty("issuer").GetString().Should().Be(issuer);
        root.GetProperty("jwks_uri").GetString().Should().Be(
            "https://gateway.example.com:6001/.well-known/jwks.json",
            "the derived jwks_uri must not contain a double slash");
    }

    // ── Helpers ──
    private static async Task<IHost> CreateHostAsync(string? issuer) =>
        await new HostBuilder()
            .ConfigureAppConfiguration(configurationBuilder =>
            {
                var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                if (issuer is not null)
                {
                    values["Jwt:Issuer"] = issuer;
                }

                configurationBuilder.AddInMemoryCollection(values);
            })
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .UseEnvironment(Environments.Production)
                .ConfigureServices(services => services.AddRouting())
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapOidcDiscoveryEndpoint());
                }))
            .StartAsync();
}
