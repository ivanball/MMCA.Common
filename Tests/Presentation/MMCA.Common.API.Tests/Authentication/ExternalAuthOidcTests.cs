using System.Security.Claims;
using AspNet.Security.OAuth.Apple;
using AspNet.Security.OAuth.GitHub;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authentication;

namespace MMCA.Common.API.Tests.Authentication;

/// <summary>
/// Generic OIDC providers read from <c>OAuth:Oidc:&lt;Name&gt;</c> by
/// <see cref="ExternalAuthExtensions.AddExternalAuthProviders"/>: no section leaves today's schemes
/// untouched, a section missing a required key fails startup naming that key, the reserved built-in
/// names are refused, and the token-validated step hands the OAuth controller a provider key and an
/// <c>email_verified</c> claim it can trust.
/// </summary>
public sealed class ExternalAuthOidcTests
{
    private const string EmailVerifiedClaimType = "email_verified";

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> ContosoConfig(bool trustEmail = false) => new(StringComparer.Ordinal)
    {
        ["OAuth:Oidc:contoso:Authority"] = "https://login.contoso.example/tenant-1/v2.0",
        ["OAuth:Oidc:contoso:ClientId"] = "contoso-client-id",
        ["OAuth:Oidc:contoso:ClientSecret"] = "contoso-client-secret",
        ["OAuth:Oidc:contoso:DisplayName"] = "Contoso",
        ["OAuth:Oidc:contoso:Domains:0"] = "contoso.com",
        ["OAuth:Oidc:contoso:TrustEmail"] = trustEmail ? "true" : "false",
    };

    private static async Task<HashSet<string>> RegisteredSchemeNamesAsync(Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication();
        services.AddExternalAuthProviders(BuildConfiguration(config));
        await using var provider = services.BuildServiceProvider();
        var schemes = await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        return schemes.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<OpenIdConnectOptions> ResolveOidcOptionsAsync(Dictionary<string, string?> config, string name)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExternalAuthProviders(BuildConfiguration(config));
        await using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(name);
    }

    // Runs the provider's token-validated step over the principal the OIDC handler would build from
    // the ID token (raw JWT claim names: the handler is configured not to remap them).
    private static async Task<ClaimsPrincipal> RunTokenValidatedAsync(
        Dictionary<string, string?> config,
        string name,
        params Claim[] tokenClaims)
    {
        var options = await ResolveOidcOptionsAsync(config, name);
        var scheme = new AuthenticationScheme(name, displayName: null, typeof(OpenIdConnectHandler));
        var context = new TokenValidatedContext(
            new DefaultHttpContext(),
            scheme,
            options,
            new ClaimsPrincipal(new ClaimsIdentity(tokenClaims, "AuthenticationTypes.Federation")),
            new AuthenticationProperties());

        await options.Events.TokenValidated(context);

        return context.Principal!;
    }

    // ── No OAuth:Oidc: the same schemes as today ──
    public static TheoryData<bool, bool, string[]> ConfigurationsWithoutOidc() => new()
    {
        { false, false, [] },
        { true, false, [GoogleDefaults.AuthenticationScheme, ExternalAuthExtensions.ExternalLoginScheme] },
        {
            true,
            true,
            [
                GoogleDefaults.AuthenticationScheme,
                GitHubAuthenticationDefaults.AuthenticationScheme,
                AppleAuthenticationDefaults.AuthenticationScheme,
                ExternalAuthExtensions.ExternalLoginScheme,
            ]
        },
    };

    [Theory]
    [MemberData(nameof(ConfigurationsWithoutOidc))]
    public async Task AddExternalAuthProviders_WithoutAnOidcSection_RegistersExactlyTodaysSchemes(
        bool withGoogle,
        bool withGitHubAndApple,
        string[] expected)
    {
        var config = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (withGoogle)
        {
            config["OAuth:Google:ClientId"] = "google-client-id";
            config["OAuth:Google:ClientSecret"] = "google-client-secret";
        }

        if (withGitHubAndApple)
        {
            config["OAuth:GitHub:ClientId"] = "github-client-id";
            config["OAuth:GitHub:ClientSecret"] = "github-client-secret";
            config["OAuth:Apple:ClientId"] = "com.example.app.signin";
            config["OAuth:Apple:TeamId"] = "TEAM123456";
            config["OAuth:Apple:KeyId"] = "KEY1234567";
            config["OAuth:Apple:PrivateKeyPem"] = "-----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY-----";
        }

        var names = await RegisteredSchemeNamesAsync(config);

        names.Should().BeEquivalentTo(expected, "the configuration has no OAuth:Oidc section");
    }

    // ── A configured section becomes a code-flow + PKCE OIDC scheme on the external-login cookie ──
    [Fact]
    public async Task AddExternalAuthProviders_WithAnOidcSection_RegistersACodeFlowPkceSchemeOnTheExternalLoginCookie()
    {
        var names = await RegisteredSchemeNamesAsync(ContosoConfig());
        var options = await ResolveOidcOptionsAsync(ContosoConfig(), "contoso");

        names.Should().Contain(["contoso", ExternalAuthExtensions.ExternalLoginScheme]);
        options.Authority.Should().Be("https://login.contoso.example/tenant-1/v2.0");
        options.ClientId.Should().Be("contoso-client-id");
        options.ClientSecret.Should().Be("contoso-client-secret");
        options.ResponseType.Should().Be("code");
        options.UsePkce.Should().BeTrue();
        options.SignInScheme.Should().Be(ExternalAuthExtensions.ExternalLoginScheme);
        options.CallbackPath.Should().Be(new PathString("/auth/callback/contoso"));
    }

    // ── A section missing a required key fails startup naming the key ──
    [Theory]
    [InlineData("ClientSecret")]
    [InlineData("Authority")]
    public async Task AddExternalAuthProviders_OidcSectionMissingARequiredKey_FailsStartupNamingTheKey(string missingKey)
    {
        var config = ContosoConfig();
        config.Remove($"OAuth:Oidc:contoso:{missingKey}");

        var act = async () => await ResolveOidcOptionsAsync(config, "contoso");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*OAuth:Oidc:contoso:{missingKey}*");
    }

    // ── The built-in provider names are reserved ──
    [Theory]
    [InlineData("google")]
    [InlineData("github")]
    [InlineData("apple")]
    [InlineData("Google")]
    public async Task AddExternalAuthProviders_OidcSectionNamedAfterABuiltInProvider_IsRefused(string reservedName)
    {
        var config = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"OAuth:Oidc:{reservedName}:Authority"] = "https://idp.example",
            [$"OAuth:Oidc:{reservedName}:ClientId"] = "client-id",
            [$"OAuth:Oidc:{reservedName}:ClientSecret"] = "client-secret",
        };

        var act = async () => await RegisteredSchemeNamesAsync(config);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{reservedName}*reserved*");
    }

    // ── ProviderKey: tid|oid for Entra-style tokens, iss|sub otherwise, never the email ──
    [Fact]
    public async Task TokenValidated_EntraStyleClaims_GiveTheProviderKeyTenantPipeObject()
    {
        var principal = await RunTokenValidatedAsync(
            ContosoConfig(),
            "contoso",
            new Claim("iss", "https://login.microsoftonline.com/tenant-guid/v2.0"),
            new Claim("sub", "pairwise-subject"),
            new Claim("tid", "tenant-guid"),
            new Claim("oid", "object-guid"),
            new Claim("email", "jane@contoso.com"));

        (principal.FindFirst(ClaimTypes.NameIdentifier)?.Value).Should().Be("tenant-guid|object-guid");
    }

    [Fact]
    public async Task TokenValidated_GenericClaims_GiveTheProviderKeyIssuerPipeSubject()
    {
        var principal = await RunTokenValidatedAsync(
            ContosoConfig(),
            "contoso",
            new Claim("iss", "https://idp.example.org"),
            new Claim("sub", "00u1abcd"),
            new Claim("email", "jane@example.org"));

        (principal.FindFirst(ClaimTypes.NameIdentifier)?.Value).Should().Be("https://idp.example.org|00u1abcd");
    }

    [Fact]
    public async Task TokenValidated_OnlyOneOfTidAndOid_FallsBackToIssuerPipeSubject()
    {
        var principal = await RunTokenValidatedAsync(
            ContosoConfig(),
            "contoso",
            new Claim("iss", "https://idp.example.org"),
            new Claim("sub", "subject-1"),
            new Claim("tid", "tenant-guid"),
            new Claim("email", "jane@example.org"));

        (principal.FindFirst(ClaimTypes.NameIdentifier)?.Value).Should().Be("https://idp.example.org|subject-1");
    }

    [Fact]
    public async Task TokenValidated_ExposesTheEmailAndTheProviderNameTheOAuthControllerReads()
    {
        var principal = await RunTokenValidatedAsync(
            ContosoConfig(),
            "contoso",
            new Claim("iss", "https://idp.example.org"),
            new Claim("sub", "subject-1"),
            new Claim("email", "jane@example.org"),
            new Claim("given_name", "Jane"),
            new Claim("family_name", "Doe"));

        (principal.Identity?.AuthenticationType).Should().Be("contoso", "the controller passes it as the login provider");
        (principal.FindFirst(ClaimTypes.Email)?.Value).Should().Be("jane@example.org");
        (principal.FindFirst(ClaimTypes.GivenName)?.Value).Should().Be("Jane");
        (principal.FindFirst(ClaimTypes.Surname)?.Value).Should().Be("Doe");
        (principal.FindFirst(ClaimTypes.NameIdentifier)?.Value).Should().NotContain("jane@example.org");
    }

    // ── email_verified passes only with TrustEmail AND the token asserting it ──
    [Theory]
    [InlineData(true, "true", "true")]
    [InlineData(false, "true", null)]
    [InlineData(true, "false", null)]
    [InlineData(true, null, null)]
    [InlineData(false, null, null)]
    public async Task TokenValidated_EmailVerified_PassesOnlyWithTrustEmailAndTheTokenAssertingIt(
        bool trustEmail,
        string? tokenEmailVerified,
        string? expected)
    {
        var claims = new List<Claim>
        {
            new("iss", "https://idp.example.org"),
            new("sub", "subject-1"),
            new("email", "jane@example.org"),
        };
        if (tokenEmailVerified is not null)
        {
            claims.Add(new Claim(EmailVerifiedClaimType, tokenEmailVerified, ClaimValueTypes.Boolean));
        }

        var principal = await RunTokenValidatedAsync(ContosoConfig(trustEmail), "contoso", [.. claims]);

        (principal.FindFirst(EmailVerifiedClaimType)?.Value).Should().Be(expected);
    }
}
