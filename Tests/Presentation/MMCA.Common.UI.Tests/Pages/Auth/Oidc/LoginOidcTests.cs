using System.Text.Json;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Auth.ExternalProviders;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Navigation;
using MMCA.Common.UI.Services.Preferences;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth.Oidc;

/// <summary>
/// The sign-in page with configured OIDC providers: one button per provider that starts the
/// challenge at <c>/auth/oauth/{name}</c>, and, only when a provider declares email domains, an
/// email-first "Sign in with your organization" step that asks the API which provider owns the
/// typed address.
/// </summary>
public sealed class LoginOidcTests : BunitTestBase
{
    private const string WorkEmail = "jane@contoso.com";

    private readonly Mock<IOAuthUISettings> _settings = new();
    private readonly Mock<IOidcDiscoveryUIService> _discovery = new();

    public LoginOidcTests()
    {
        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton(new OAuthFlowStateStore(new InMemoryCacheStore()));
        Services.AddSingleton(_settings.Object);
        Services.AddSingleton(Options.Create(new ApiSettings { ApiEndpoint = "https://api.example.com/" }));
        Services.AddSingleton(new Mock<IUserPreferenceReader>().Object);
        Services.AddSingleton(_discovery.Object);
    }

    private BunitNavigationManager Navigation => Services.GetRequiredService<BunitNavigationManager>();

    private void GivenProviders(params OidcProviderOption[] providers) =>
        _settings.SetupGet(s => s.OidcProviders).Returns(providers);

    [Fact]
    public async Task EachOidcProvider_RendersAButtonThatStartsItsChallenge()
    {
        GivenProviders(
            new OidcProviderOption("contoso", "Contoso", UsesDomains: false),
            new OidcProviderOption("okta", "Fabrikam Okta", UsesDomains: false));
        Navigation.NavigateTo("/login");
        var cut = RenderUnderTest<Login>(_ => { });

        cut.FindAll("button").Select(b => b.GetAttribute("aria-label"))
            .Should().Contain(["Sign in with Contoso", "Sign in with Fabrikam Okta"]);

        await ProviderButton(cut, "Sign in with Contoso").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Navigation.Uri.Should().StartWith("https://api.example.com/auth/oauth/contoso");
        Navigation.Uri.Should().Contain("state=");
    }

    [Fact]
    public void WithoutAnyProviderDeclaringDomains_TheOrganizationStepIsAbsent()
    {
        GivenProviders(new OidcProviderOption("contoso", "Contoso", UsesDomains: false));
        Navigation.NavigateTo("/login");

        var cut = RenderUnderTest<Login>(_ => { });

        cut.FindAll("[data-testid='login-organization-sign-in']").Should().BeEmpty();
    }

    [Fact]
    public void WithNoOidcProviders_NothingOidcRenders()
    {
        Navigation.NavigateTo("/login");

        var cut = RenderUnderTest<Login>(_ => { });

        cut.FindAll("[data-testid='login-organization-sign-in']").Should().BeEmpty();
        cut.FindAll("[data-testid^='login-oidc-']").Should().BeEmpty();
    }

    [Fact]
    public async Task OrganizationStep_WithAMatchingDomain_StartsThatProvidersChallenge()
    {
        GivenProviders(new OidcProviderOption("contoso", "Contoso", UsesDomains: true));
        _discovery
            .Setup(x => x.DiscoverAsync(WorkEmail, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OidcDiscoveryResponse("contoso", "Contoso"));
        AuthEmailPrefill.NavigateWithEmail(Navigation, "/login", WorkEmail);
        var cut = RenderUnderTest<Login>(_ => { });

        var step = cut.Find("[data-testid='login-organization-sign-in']");
        step.TextContent.Should().Contain("Sign in with your organization");
        await step.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        await cut.WaitForAssertionAsync(() => Navigation.Uri.Should().StartWith("https://api.example.com/auth/oauth/contoso"));
    }

    [Fact]
    public void OrganizationStep_WithNoMatchingDomain_StaysAndExplains()
    {
        GivenProviders(new OidcProviderOption("contoso", "Contoso", UsesDomains: true));
        _discovery
            .Setup(x => x.DiscoverAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OidcDiscoveryResponse?)null);
        AuthEmailPrefill.NavigateWithEmail(Navigation, "/login", "jane@fabrikam.com");
        var cut = RenderUnderTest<Login>(_ => { });

        cut.Find("[data-testid='login-organization-sign-in']").Click();

        cut.WaitForAssertion(() => cut.Find("[role='alert']").TextContent
            .Should().Contain("No organization sign-in is set up for that email address."));
        new Uri(Navigation.Uri).AbsolutePath.Should().Be("/login");
    }

    [Fact]
    public void OrganizationStep_WithNoAddress_AsksForOneAndDiscoversNothing()
    {
        GivenProviders(new OidcProviderOption("contoso", "Contoso", UsesDomains: true));
        Navigation.NavigateTo("/login");
        var cut = RenderUnderTest<Login>(_ => { });

        cut.Find("[data-testid='login-organization-sign-in']").Click();

        cut.WaitForAssertion(() => cut.Find("[role='alert']").TextContent
            .Should().Contain("Enter your work email address first."));
        _discovery.Verify(x => x.DiscoverAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    private static AngleSharp.Dom.IElement ProviderButton(IRenderedComponent<Login> cut, string label) =>
        cut.FindAll("button").Single(e => e.GetAttribute("aria-label") == label);

    private sealed class InMemoryCacheStore : ILocalCacheStore
    {
        private readonly Dictionary<string, string> _entries = [];

        public bool IsAvailable => true;

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            _entries[key] = JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _entries.Remove(key);
            return Task.CompletedTask;
        }
    }
}
