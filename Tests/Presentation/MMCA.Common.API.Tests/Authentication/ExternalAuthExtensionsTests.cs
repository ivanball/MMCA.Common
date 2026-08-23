using AspNet.Security.OAuth.Apple;
using AspNet.Security.OAuth.GitHub;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authentication;

namespace MMCA.Common.API.Tests.Authentication;

/// <summary>
/// Verifies <see cref="ExternalAuthExtensions.AddExternalAuthProviders"/>: registration stays
/// inert without OAuth configuration, each provider scheme is gated on its ClientId, a missing
/// ClientSecret fails fast, and the provider/cookie options carry the callback paths, sign-in
/// scheme, and cookie hardening the OAuth controller flow depends on.
/// </summary>
public sealed class ExternalAuthExtensionsTests
{
    // ── Helpers ──
    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    private static Dictionary<string, string?> GoogleConfig() => new(StringComparer.Ordinal)
    {
        ["OAuth:Google:ClientId"] = "google-client-id",
        ["OAuth:Google:ClientSecret"] = "google-client-secret",
    };

    private static Dictionary<string, string?> GitHubConfig() => new(StringComparer.Ordinal)
    {
        ["OAuth:GitHub:ClientId"] = "github-client-id",
        ["OAuth:GitHub:ClientSecret"] = "github-client-secret",
    };

    private static Dictionary<string, string?> AppleConfig() => new(StringComparer.Ordinal)
    {
        ["OAuth:Apple:ClientId"] = "com.example.app.signin",
        ["OAuth:Apple:TeamId"] = "TEAM123456",
        ["OAuth:Apple:KeyId"] = "KEY1234567",
        ["OAuth:Apple:PrivateKeyPem"] = "-----BEGIN PRIVATE KEY-----\nnot-a-real-key\n-----END PRIVATE KEY-----",
    };

    // ── No provider configured: inert ──
    [Fact]
    public void AddExternalAuthProviders_WithNoProviderConfigured_AddsNoRegistrations()
    {
        var services = CreateServices();
        var countBefore = services.Count;

        services.AddExternalAuthProviders(BuildConfiguration([]));

        services.Count.Should().Be(
            countBefore,
            "a host without OAuth secrets must keep the JWT-only authentication pipeline untouched");
    }

    // ── Google configured ──
    [Fact]
    public async Task AddExternalAuthProviders_WithGoogleConfigured_RegistersGoogleAndCookieSchemesOnly()
    {
        var services = CreateServices();
        services.AddExternalAuthProviders(BuildConfiguration(GoogleConfig()));
        await using var provider = services.BuildServiceProvider();
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        (await schemeProvider.GetSchemeAsync(GoogleDefaults.AuthenticationScheme)).Should().NotBeNull();
        (await schemeProvider.GetSchemeAsync(ExternalAuthExtensions.ExternalLoginScheme)).Should().NotBeNull();
        (await schemeProvider.GetSchemeAsync(GitHubAuthenticationDefaults.AuthenticationScheme)).Should().BeNull(
            "GitHub has no ClientId configured, so its scheme must not be registered");
    }

    [Fact]
    public async Task AddExternalAuthProviders_GoogleOptions_AreWiredForTheExternalLoginFlow()
    {
        var services = CreateServices();
        services.AddExternalAuthProviders(BuildConfiguration(GoogleConfig()));
        await using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptionsMonitor<GoogleOptions>>()
            .Get(GoogleDefaults.AuthenticationScheme);

        options.ClientId.Should().Be("google-client-id");
        options.ClientSecret.Should().Be("google-client-secret");
        options.SignInScheme.Should().Be(ExternalAuthExtensions.ExternalLoginScheme);
        options.CallbackPath.Should().Be(new PathString("/auth/callback/google"));
        options.SaveTokens.Should().BeTrue();
    }

    // ── GitHub configured ──
    [Fact]
    public async Task AddExternalAuthProviders_WithGitHubConfigured_RegistersGitHubSchemeOnly()
    {
        var services = CreateServices();
        services.AddExternalAuthProviders(BuildConfiguration(GitHubConfig()));
        await using var provider = services.BuildServiceProvider();
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        (await schemeProvider.GetSchemeAsync(GitHubAuthenticationDefaults.AuthenticationScheme)).Should().NotBeNull();
        (await schemeProvider.GetSchemeAsync(ExternalAuthExtensions.ExternalLoginScheme)).Should().NotBeNull();
        (await schemeProvider.GetSchemeAsync(GoogleDefaults.AuthenticationScheme)).Should().BeNull();
    }

    [Fact]
    public async Task AddExternalAuthProviders_GitHubOptions_RequestEmailScopeAndCallbackPath()
    {
        var services = CreateServices();
        services.AddExternalAuthProviders(BuildConfiguration(GitHubConfig()));
        await using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptionsMonitor<GitHubAuthenticationOptions>>()
            .Get(GitHubAuthenticationDefaults.AuthenticationScheme);

        options.SignInScheme.Should().Be(ExternalAuthExtensions.ExternalLoginScheme);
        options.CallbackPath.Should().Be(new PathString("/auth/callback/github"));
        options.SaveTokens.Should().BeTrue();
        options.Scope.Should().Contain(
            "user:email",
            "GitHub omits the email on the default scope and CompleteAsync requires the email claim");
    }

    // ── Apple configured ──
    [Fact]
    public async Task AddExternalAuthProviders_WithAppleConfigured_RegistersAppleAndCookieSchemesOnly()
    {
        var services = CreateServices();
        services.AddExternalAuthProviders(BuildConfiguration(AppleConfig()));
        await using var provider = services.BuildServiceProvider();
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        (await schemeProvider.GetSchemeAsync(AppleAuthenticationDefaults.AuthenticationScheme)).Should().NotBeNull();
        (await schemeProvider.GetSchemeAsync(ExternalAuthExtensions.ExternalLoginScheme)).Should().NotBeNull();
        (await schemeProvider.GetSchemeAsync(GoogleDefaults.AuthenticationScheme)).Should().BeNull();
        (await schemeProvider.GetSchemeAsync(GitHubAuthenticationDefaults.AuthenticationScheme)).Should().BeNull();
    }

    [Fact]
    public async Task AddExternalAuthProviders_AppleOptions_AreWiredForTheExternalLoginFlow()
    {
        var services = CreateServices();
        services.AddExternalAuthProviders(BuildConfiguration(AppleConfig()));
        await using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptionsMonitor<AppleAuthenticationOptions>>()
            .Get(AppleAuthenticationDefaults.AuthenticationScheme);

        options.ClientId.Should().Be("com.example.app.signin");
        options.TeamId.Should().Be("TEAM123456");
        options.KeyId.Should().Be("KEY1234567");
        options.GenerateClientSecret.Should().BeTrue(
            "Apple has no static client secret; the handler must mint the ES256 client-secret JWT");
        options.PrivateKey.Should().NotBeNull();
        options.SignInScheme.Should().Be(ExternalAuthExtensions.ExternalLoginScheme);
        options.CallbackPath.Should().Be(new PathString("/auth/callback/apple"));
        options.SaveTokens.Should().BeTrue();
    }

    [Fact]
    public async Task AddExternalAuthProviders_ApplePrivateKeyCallback_ReturnsConfiguredPem()
    {
        var services = CreateServices();
        services.AddExternalAuthProviders(BuildConfiguration(AppleConfig()));
        await using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptionsMonitor<AppleAuthenticationOptions>>()
            .Get(AppleAuthenticationDefaults.AuthenticationScheme);

        var pem = await options.PrivateKey!("KEY1234567", CancellationToken.None);

        pem.ToString().Should().Contain("BEGIN PRIVATE KEY");
    }

    // ── External-login cookie hardening ──
    [Fact]
    public async Task AddExternalAuthProviders_ExternalLoginCookie_IsHttpOnlyLaxAndShortLived()
    {
        var services = CreateServices();
        services.AddExternalAuthProviders(BuildConfiguration(GoogleConfig()));
        await using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(ExternalAuthExtensions.ExternalLoginScheme);

        options.Cookie.Name.Should().Be("mmca_external_login");
        options.Cookie.HttpOnly.Should().BeTrue("the external principal must not be readable from script");
        options.Cookie.SameSite.Should().Be(SameSiteMode.Lax);
        options.ExpireTimeSpan.Should().Be(TimeSpan.FromMinutes(10));
    }

    // ── Missing ClientSecret fails fast ──
    [Fact]
    public async Task AddExternalAuthProviders_GoogleClientIdWithoutSecret_ThrowsOnOptionsResolution()
    {
        var services = CreateServices();
        services.AddExternalAuthProviders(BuildConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OAuth:Google:ClientId"] = "google-client-id",
        }));
        await using var provider = services.BuildServiceProvider();

        var act = () => provider
            .GetRequiredService<IOptionsMonitor<GoogleOptions>>()
            .Get(GoogleDefaults.AuthenticationScheme);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OAuth:Google:ClientSecret is required*");
    }

    [Fact]
    public async Task AddExternalAuthProviders_GitHubClientIdWithoutSecret_ThrowsOnOptionsResolution()
    {
        var services = CreateServices();
        services.AddExternalAuthProviders(BuildConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OAuth:GitHub:ClientId"] = "github-client-id",
        }));
        await using var provider = services.BuildServiceProvider();

        var act = () => provider
            .GetRequiredService<IOptionsMonitor<GitHubAuthenticationOptions>>()
            .Get(GitHubAuthenticationDefaults.AuthenticationScheme);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OAuth:GitHub:ClientSecret is required*");
    }

    [Theory]
    [InlineData("TeamId")]
    [InlineData("KeyId")]
    [InlineData("PrivateKeyPem")]
    public async Task AddExternalAuthProviders_AppleWithMissingSigningConfig_ThrowsOnOptionsResolution(string missingKey)
    {
        var config = AppleConfig();
        config.Remove($"OAuth:Apple:{missingKey}");
        var services = CreateServices();
        services.AddExternalAuthProviders(BuildConfiguration(config));
        await using var provider = services.BuildServiceProvider();

        var act = () => provider
            .GetRequiredService<IOptionsMonitor<AppleAuthenticationOptions>>()
            .Get(AppleAuthenticationDefaults.AuthenticationScheme);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*OAuth:Apple:{missingKey} is required*");
    }
}
