using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Tests.Services;

/// <summary>
/// Unit tests for <see cref="NavigationPublicLinkBuilder"/>: the browser-origin default behind the
/// share, copy-link and QR affordances. Every result must be absolute, since the whole point of the
/// abstraction is that a shared link opens for someone who is not inside the app.
/// </summary>
public sealed class NavigationPublicLinkBuilderTests : BunitTestBase
{
    private NavigationPublicLinkBuilder Builder =>
        new(Services.GetRequiredService<NavigationManager>());

    [Theory]
    [InlineData("/sessions/42")]
    [InlineData("sessions/42")]
    public void BuildsAnAbsoluteUrlOnTheBrowserOrigin(string relativePath) =>
        Builder.BuildAbsolute(relativePath).Should().Be(new Uri("http://localhost/sessions/42"));

    [Fact]
    public void PreservesTheQueryString() =>
        Builder.BuildAbsolute("/sessions?day=2").ToString().Should().Be("http://localhost/sessions?day=2");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsABlankPath(string relativePath)
    {
        var builder = Builder;

        var act = () => builder.BuildAbsolute(relativePath);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddUIShared_RegistersTheBrowserOriginDefault()
    {
        // Every head gets a working builder without registering one; a MAUI head replaces it
        // afterwards with the public-site builder (last registration wins).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Api:ApiEndpoint"] = "https://localhost:6001" })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ITokenStorageService>(new StubTokenStorageService());
        services.AddUIShared(configuration);

        // Asserted on the descriptor rather than a resolved instance: the implementation needs a
        // NavigationManager, which only a rendering host supplies.
        var descriptor = services.Single(s => s.ServiceType == typeof(IPublicLinkBuilder));
        descriptor.ImplementationType.Should().Be<NavigationPublicLinkBuilder>();
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }
}
