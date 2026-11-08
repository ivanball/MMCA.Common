using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using MMCA.Common.UI.Services.Auth.OAuth;

namespace MMCA.Common.UI.Tests.Services.Auth.OAuth;

/// <summary>
/// <see cref="ConfigurationOAuthUISettings"/> exposes the configured OIDC providers to the sign-in
/// page: a server host lists a provider whose <c>OAuth:Oidc:&lt;Name&gt;:ClientId</c> is set, a WASM
/// client lists one its runtime configuration flags <c>Enabled</c> (it never receives the client id),
/// and the built-in provider names are never listed as OIDC providers.
/// </summary>
public sealed class ConfigurationOAuthUISettingsOidcTests
{
    private static ConfigurationOAuthUISettings Create(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void WithoutAnOidcSection_ListsNoOidcProviders()
    {
        var settings = Create(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OAuth:Google:ClientId"] = "google-client-id",
        });

        settings.OidcProviders.Should().BeEmpty();
        settings.GoogleEnabled.Should().BeTrue();
    }

    [Fact]
    public void ServerHost_ListsEachProviderWithAClientId_WithItsDisplayNameAndDomainFlag()
    {
        var settings = Create(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OAuth:Oidc:contoso:ClientId"] = "contoso-client-id",
            ["OAuth:Oidc:contoso:DisplayName"] = "Contoso",
            ["OAuth:Oidc:contoso:Domains:0"] = "contoso.com",
            ["OAuth:Oidc:okta:ClientId"] = "okta-client-id",
            ["OAuth:Oidc:disabled:DisplayName"] = "No client id",
        });

        settings.OidcProviders.Should().BeEquivalentTo(
        [
            new OidcProviderOption("contoso", "Contoso", UsesDomains: true),
            new OidcProviderOption("okta", "okta", UsesDomains: false),
        ]);
    }

    [Fact]
    public void WasmClient_ListsAProviderFlaggedEnabled()
    {
        var settings = Create(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OAuth:Oidc:contoso:Enabled"] = "true",
            ["OAuth:Oidc:contoso:DisplayName"] = "Contoso",
            ["OAuth:Oidc:contoso:UsesDomains"] = "true",
        });

        settings.OidcProviders.Should().BeEquivalentTo([new OidcProviderOption("contoso", "Contoso", UsesDomains: true)]);
    }

    [Fact]
    public void ReservedBuiltInNames_AreNeverListedAsOidcProviders()
    {
        var settings = Create(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OAuth:Oidc:google:ClientId"] = "x",
            ["OAuth:Oidc:GitHub:ClientId"] = "x",
            ["OAuth:Oidc:apple:ClientId"] = "x",
        });

        settings.OidcProviders.Should().BeEmpty();
    }
}
