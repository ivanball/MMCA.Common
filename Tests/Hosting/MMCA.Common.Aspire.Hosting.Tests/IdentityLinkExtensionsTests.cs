using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Controllers;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.EmailConfirmation;
using MMCA.Common.Application.Auth.SignInLinks;
using MMCA.Common.Infrastructure.Auth;

namespace MMCA.Common.Aspire.Hosting.Tests;

/// <summary>
/// Unit tests for <c>WithUiLinks</c> and <c>WithIssuer</c>, the two Identity-side AppHost helpers that
/// replace hand-spelled environment keys: the four links Identity puts in emails and OAuth redirects
/// (all rooted at the UI's https endpoint) and the JWT issuer (the gateway's https endpoint).
/// <para>
/// <c>MMCA.Common.Aspire.Hosting</c> does not reference the packages that own those settings, so it
/// mirrors their section and property names as internal constants. This test project references the
/// owners, so every emitted key is cross-asserted against the settings type that binds it, and a
/// rename there cannot silently orphan the AppHost wiring.
/// </para>
/// </summary>
public sealed class IdentityLinkExtensionsTests
{
    private const string ResetPasswordPath = "/reset-password";

    private const string ConfirmEmailPath = "/confirm-email";

    private const string SignInLinkPath = "/auth/sign-in-link";

    [Fact]
    public void NullArguments_Throw()
    {
        var model = Arrange();
        IResourceBuilder<ProjectResource> nullIdentity = null!;

        FluentActions.Invoking(() => nullIdentity.WithUiLinks(model.Ui))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => model.Identity.WithUiLinks(null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => nullIdentity.WithIssuer(model.Gateway))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => model.Identity.WithIssuer(null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task WithUiLinks_SetsTheFourKeys()
    {
        var model = Arrange();
        var control = model.Builder.AddResource(new ProjectResource("control"))
            .WithEnvironment("UiHttps", model.Ui.GetEndpoint("https"));

        model.Identity.WithUiLinks(model.Ui);

        var uiHttps = (await EnvironmentOf(model.Builder, control.Resource))["UiHttps"];
        var environment = await EnvironmentOf(model.Builder, model.Identity.Resource);

        environment.Should().ContainKey("OAuth__UIBaseUrl")
            .WhoseValue.Should().Be(uiHttps, because: "the OAuth redirect base is the bare UI https endpoint, with no path");
        environment.Should().ContainKey("PasswordReset__ResetUrl")
            .WhoseValue.Should().Be(uiHttps + ResetPasswordPath);
        environment.Should().ContainKey("Authentication__EmailConfirmation__ConfirmationUrl")
            .WhoseValue.Should().Be(uiHttps + ConfirmEmailPath);
        environment.Should().ContainKey("Authentication__SignInLinks__SignInUrl")
            .WhoseValue.Should().Be(uiHttps + SignInLinkPath);
    }

    [Fact]
    public async Task WithUiLinks_KeysDeriveFromTheSettingsClasses()
    {
        var model = Arrange();

        model.Identity.WithUiLinks(model.Ui);

        var environment = await EnvironmentOf(model.Builder, model.Identity.Resource);

        environment.Should().ContainKey(Key(PasswordResetSettings.SectionName, nameof(PasswordResetSettings.ResetUrl)));
        environment.Should().ContainKey(Key(EmailConfirmationSettings.SectionName, nameof(EmailConfirmationSettings.ConfirmationUrl)));
        environment.Should().ContainKey(Key(SignInLinkSettings.SectionName, nameof(SignInLinkSettings.SignInUrl)));
        environment.Should().ContainKey(OAuthControllerBase.UIBaseUrlConfigKey.Replace(":", "__", StringComparison.Ordinal));
    }

    [Fact]
    public void EnvironmentKey_MapsAConfigurationSectionToItsEnvironmentForm()
    {
        IdentityLinkExtensions.EnvironmentKey(PasswordResetSettings.SectionName, nameof(PasswordResetSettings.ResetUrl))
            .Should().Be("PasswordReset__ResetUrl");
        IdentityLinkExtensions.EnvironmentKey(SignInLinkSettings.SectionName, nameof(SignInLinkSettings.SignInUrl))
            .Should().Be("Authentication__SignInLinks__SignInUrl");
    }

    // The internal constants are mirrors of names owned elsewhere; this is the check that keeps them
    // honest (design 23(a): "mirrors, cross-checked by tests").
    [Fact]
    public void MirroredConstants_MatchTheOwningSettings()
    {
        IdentityLinkExtensions.PasswordResetSection.Should().Be(PasswordResetSettings.SectionName);
        IdentityLinkExtensions.EmailConfirmationSection.Should().Be(EmailConfirmationSettings.SectionName);
        IdentityLinkExtensions.SignInLinksSection.Should().Be(SignInLinkSettings.SectionName);
        IdentityLinkExtensions.JwtSection.Should().Be(JwtSettings.SectionName);
        $"{IdentityLinkExtensions.OAuthSection}:{IdentityLinkExtensions.OAuthUiBaseUrlProperty}"
            .Should().Be(OAuthControllerBase.UIBaseUrlConfigKey);
        IdentityLinkExtensions.ResetPasswordPath.Should().Be(ResetPasswordPath);
        IdentityLinkExtensions.ConfirmEmailPath.Should().Be(ConfirmEmailPath);
        IdentityLinkExtensions.SignInLinkPath.Should().Be(SignInLinkPath);
    }

    [Fact]
    public async Task WithIssuer_SetsJwtIssuer_DerivedFromJwtSettings()
    {
        var model = Arrange();
        var control = model.Builder.AddResource(new ProjectResource("control"))
            .WithEnvironment("GatewayHttps", model.Gateway.GetEndpoint("https"));

        model.Identity.WithIssuer(model.Gateway);

        var gatewayHttps = (await EnvironmentOf(model.Builder, control.Resource))["GatewayHttps"];
        var environment = await EnvironmentOf(model.Builder, model.Identity.Resource);

        environment.Should().ContainKey(Key(JwtSettings.SectionName, nameof(JwtSettings.Issuer)))
            .WhoseValue.Should().Be(gatewayHttps);
    }

    // WithReference to a project would inject service-discovery variables (and add a parent edge the
    // ADC Bicep parity test counts); WaitFor would add a wait annotation. Neither helper may do either:
    // they only write configuration values.
    [Fact]
    public async Task NeitherHelper_AddsAReferenceOrWait()
    {
        var model = Arrange();

        model.Identity.WithUiLinks(model.Ui).WithIssuer(model.Gateway);

        model.Identity.Resource.Annotations.OfType<WaitAnnotation>().Should().BeEmpty();

        var environment = await EnvironmentOf(model.Builder, model.Identity.Resource);
        environment.Keys.Should().NotContain(key => key.StartsWith("services__", StringComparison.Ordinal));
        environment.Keys.Should().NotContain(key => key.StartsWith("ConnectionStrings__", StringComparison.Ordinal));
    }

    [Fact]
    public void ReturnsTheSameBuilder()
    {
        var model = Arrange();

        model.Identity.WithUiLinks(model.Ui).Should().BeSameAs(model.Identity);
        model.Identity.WithIssuer(model.Gateway).Should().BeSameAs(model.Identity);
    }

    private static string Key(string section, string property) =>
        section.Replace(":", "__", StringComparison.Ordinal) + "__" + property;

    // AddResource rather than AddProject: the helpers only need ProjectResources in the model, and
    // AddProject validates that the project file exists on disk.
    private static Model Arrange()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var identity = builder.AddResource(new ProjectResource("identity"));
        var ui = builder.AddResource(new ProjectResource("ui")).WithHttpsEndpoint(name: "https");
        var gateway = builder.AddResource(new ProjectResource("gateway")).WithHttpsEndpoint(name: "https");

        return new Model(builder, identity, ui, gateway);
    }

    /// <summary>
    /// Resolves a resource's environment variables in publish mode, where every reference stays a
    /// manifest expression rather than a value DCP happened to allocate.
    /// </summary>
    private static async Task<Dictionary<string, string>> EnvironmentOf(
        IDistributedApplicationBuilder builder,
        IResource resource)
    {
        await using ServiceProvider services = builder.Services.BuildServiceProvider();
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Publish)
            {
                Services = services,
            });

        IExecutionConfigurationResult result = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext);

        result.Exception.Should().BeNull();
        return result.EnvironmentVariables.ToDictionary(StringComparer.Ordinal);
    }

    private sealed record Model(
        IDistributedApplicationBuilder Builder,
        IResourceBuilder<ProjectResource> Identity,
        IResourceBuilder<ProjectResource> Ui,
        IResourceBuilder<ProjectResource> Gateway);
}
