using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.API.Startup;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// In-memory TestServer tests over <c>GET /.well-known/jwks.json</c> mapped by
/// <see cref="JwksEndpointExtensions"/>, wired to a real <see cref="RsaJwksProvider"/> with a
/// generated RSA key. The document must publish the RFC 7517 public fields (kid/kty/n/e, use,
/// alg) in lowercase, stay a valid empty key set when publishing is disabled, and, critically,
/// never leak private-key parameters (d, p, q, dp, dq, qi), even when a private-key PEM is
/// misconfigured as the key material.
/// </summary>
public sealed class JwksEndpointTests
{
    // ── Publishing an RSA public key ──
    [Fact]
    public async Task GetJwks_WithConfiguredRsaKey_PublishesPublicKeyFieldsInRfcForm()
    {
        using var rsa = RSA.Create(2048);
        using var host = await CreateHostAsync(CreateProvider(rsa.ExportSubjectPublicKeyInfoPem(), "test-key-1"));
        using HttpClient client = host.GetTestClient();

        using var response = await client.GetAsync(
            new Uri(JwksEndpointExtensions.DefaultJwksPath, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var keys = document.RootElement.GetProperty("keys");
        keys.GetArrayLength().Should().Be(1);

        var key = keys[0];
        key.GetProperty("kid").GetString().Should().Be("test-key-1");
        key.GetProperty("kty").GetString().Should().Be("RSA");
        key.GetProperty("use").GetString().Should().Be("sig");
        key.GetProperty("alg").GetString().Should().Be("RS256");

        RSAParameters parameters = rsa.ExportParameters(includePrivateParameters: false);
        key.GetProperty("n").GetString().Should().Be(Base64UrlEncoder.Encode(parameters.Modulus));
        key.GetProperty("e").GetString().Should().Be(Base64UrlEncoder.Encode(parameters.Exponent));
    }

    // ── CRITICAL: private key material never leaves the endpoint ──
    [Fact]
    public async Task GetJwks_EvenWhenConfiguredWithAPrivateKeyPem_NeverPublishesPrivateParameters()
    {
        // Misconfiguration scenario: an operator pastes the full PRIVATE key PEM into the
        // public-key setting. The provider must still export only the public half.
        using var rsa = RSA.Create(2048);
        using var host = await CreateHostAsync(CreateProvider(rsa.ExportRSAPrivateKeyPem(), "test-key-1"));
        using HttpClient client = host.GetTestClient();

        using var response = await client.GetAsync(
            new Uri(JwksEndpointExtensions.DefaultJwksPath, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var keys = document.RootElement.GetProperty("keys");
        keys.GetArrayLength().Should().Be(1, "the public half must still be published");
        var key = keys[0];
        key.GetProperty("n").GetString().Should().NotBeNullOrEmpty();

        // No private JWK member may appear as a JSON field ...
        string[] privateMembers = ["d", "p", "q", "dp", "dq", "qi", "k"];
        foreach (string privateMember in privateMembers)
        {
            key.TryGetProperty(privateMember, out _).Should().BeFalse(
                $"the private JWK member '{privateMember}' must never be serialized");
        }

        // "oth" (other primes info) serializes as an empty collection; it must stay empty.
        if (key.TryGetProperty("oth", out var oth))
        {
            oth.GetArrayLength().Should().Be(0, "the 'oth' private member must never carry prime data");
        }

        // ... and no private parameter VALUE may appear anywhere in the response body.
        RSAParameters privateParameters = rsa.ExportParameters(includePrivateParameters: true);
        var privateValues = new Dictionary<string, byte[]?>(StringComparer.Ordinal)
        {
            ["D"] = privateParameters.D,
            ["P"] = privateParameters.P,
            ["Q"] = privateParameters.Q,
            ["DP"] = privateParameters.DP,
            ["DQ"] = privateParameters.DQ,
            ["InverseQ"] = privateParameters.InverseQ,
        };
        foreach ((string name, byte[]? value) in privateValues)
        {
            body.Should().NotContain(
                Base64UrlEncoder.Encode(value),
                $"the base64url-encoded private RSA parameter {name} must never appear in the JWKS body");
        }
    }

    // ── Disabled publishing stays queryable ──
    [Fact]
    public async Task GetJwks_WhenPublishingDisabled_ReturnsValidEmptyKeySet()
    {
        var provider = new RsaJwksProvider(Options.Create(new JwksSettings { Enabled = false }));
        using var host = await CreateHostAsync(provider);
        using HttpClient client = host.GetTestClient();

        using var response = await client.GetAsync(
            new Uri(JwksEndpointExtensions.DefaultJwksPath, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("keys").GetArrayLength().Should().Be(
            0, "downstream services poll this endpoint and expect a valid, empty key set");
    }

    // ── Helpers ──
    private static RsaJwksProvider CreateProvider(string pem, string keyId) =>
        new(Options.Create(new JwksSettings
        {
            Enabled = true,
            KeyId = keyId,
            RsaPublicKeyPem = pem,
        }));

    private static async Task<IHost> CreateHostAsync(IJwksProvider jwksProvider) =>
        await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .UseEnvironment(Environments.Production)
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(jwksProvider);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapJwksEndpoint());
                }))
            .StartAsync();
}
