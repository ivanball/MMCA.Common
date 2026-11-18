using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MMCA.Common.UI.Web.Hosting;

namespace MMCA.Common.UI.Web.Tests.Hosting;

/// <summary>
/// Specifies <c>E2eRenderMode.Resolve</c> (item 25, rule 5): the one render-mode selector both Blazor
/// heads use from <c>App.razor</c>. Forced WebAssembly wins over forced Server, and with neither flag
/// the app renders InteractiveAuto, which is what production and local development always see.
/// </summary>
public sealed class E2eRenderModeTests
{
    /// <summary>
    /// A null flag is absent. Forced WebAssembly wins when both flags are set. A flag that is present
    /// but false selects nothing, the same as an absent one: the AppHost writes only
    /// <see langword="true"/>, but a hand-set <see langword="false"/> must not be read as "set". Both
    /// heads read the flags with <c>GetValue&lt;bool&gt;</c> today, which parses case-insensitively;
    /// moving the selector into Common keeps that, so a hand-set <c>TRUE</c> still selects.
    /// </summary>
    /// <param name="forceServer">The <c>E2E:ForceServer</c> value, or null when absent.</param>
    /// <param name="forceWebAssembly">The <c>E2E:ForceWebAssembly</c> value, or null when absent.</param>
    /// <param name="expectedMode">The render-mode type the flags select.</param>
    [Theory]
    [InlineData(null, null, typeof(InteractiveAutoRenderMode))]
    [InlineData("true", null, typeof(InteractiveServerRenderMode))]
    [InlineData(null, "true", typeof(InteractiveWebAssemblyRenderMode))]
    [InlineData("true", "true", typeof(InteractiveWebAssemblyRenderMode))]
    [InlineData("false", "false", typeof(InteractiveAutoRenderMode))]
    [InlineData(null, "TRUE", typeof(InteractiveWebAssemblyRenderMode))]
    public void Flags_SelectTheRenderMode(string? forceServer, string? forceWebAssembly, Type expectedMode)
    {
        var settings = new List<(string Key, string Value)>();
        if (forceServer is not null)
        {
            settings.Add((E2eRenderMode.ForceServerKey, forceServer));
        }

        if (forceWebAssembly is not null)
        {
            settings.Add((E2eRenderMode.ForceWebAssemblyKey, forceWebAssembly));
        }

        var mode = E2eRenderMode.Resolve(Configuration([.. settings]));

        mode.Should().BeOfType(expectedMode);
    }

    [Fact]
    public void NullConfiguration_Throws()
    {
        var act = () => E2eRenderMode.Resolve(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// The keys are the colon form of what the AppHost writes (<c>E2E__ForceWebAssembly</c>,
    /// <c>E2E__ForceServer</c>), so a rename on either side breaks this test rather than the E2E run.
    /// </summary>
    [Fact]
    public void Keys_MatchTheWireNames()
    {
        E2eRenderMode.ForceWebAssemblyKey.Should().Be("E2E:ForceWebAssembly");
        E2eRenderMode.ForceServerKey.Should().Be("E2E:ForceServer");
    }

    // -- Run 17 U-58: /Error stays static after the interactive router takes over --
    [Fact]
    public void ForRequest_APageExcludedFromInteractiveRouting_RendersStatically()
    {
        var mode = E2eRenderMode.Resolve(Configuration(), RequestFor(typeof(Components.Pages.Error)));

        mode.Should().BeNull("the interactive router does not know the framework error page");
    }

    [Fact]
    public void ForRequest_AnInteractivePage_KeepsTheHeadsRenderMode()
    {
        var mode = E2eRenderMode.Resolve(Configuration((E2eRenderMode.ForceServerKey, "true")), RequestFor(typeof(InteractivePage)));

        mode.Should().BeOfType<InteractiveServerRenderMode>();
    }

    [Fact]
    public void ErrorPage_IsExcludedFromInteractiveRouting() =>
        typeof(Components.Pages.Error)
            .GetCustomAttributes(typeof(ExcludeFromInteractiveRoutingAttribute), inherit: false)
            .Should().ContainSingle();

    // The endpoint carries the page type as metadata, as MapRazorComponents builds it.
    private static DefaultHttpContext RequestFor(Type pageType)
    {
        var metadata = new EndpointMetadataCollection(new ComponentTypeMetadata(pageType));
        var httpContext = new DefaultHttpContext();
        httpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "page"));
        return httpContext;
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();

    /// <summary>A page with no routing opt-out.</summary>
    private sealed class InteractivePage : ComponentBase;
}
