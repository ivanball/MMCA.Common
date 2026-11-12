using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Web;
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
    [Fact]
    public void NoFlags_IsInteractiveAuto()
    {
        var mode = E2eRenderMode.Resolve(Configuration());

        mode.Should().BeOfType<InteractiveAutoRenderMode>();
    }

    [Fact]
    public void ForceServer_IsInteractiveServer()
    {
        var mode = E2eRenderMode.Resolve(Configuration((E2eRenderMode.ForceServerKey, "true")));

        mode.Should().BeOfType<InteractiveServerRenderMode>();
    }

    [Fact]
    public void ForceWebAssembly_IsInteractiveWebAssembly()
    {
        var mode = E2eRenderMode.Resolve(Configuration((E2eRenderMode.ForceWebAssemblyKey, "true")));

        mode.Should().BeOfType<InteractiveWebAssemblyRenderMode>();
    }

    [Fact]
    public void BothFlags_WebAssemblyWins()
    {
        var mode = E2eRenderMode.Resolve(Configuration(
            (E2eRenderMode.ForceServerKey, "true"),
            (E2eRenderMode.ForceWebAssemblyKey, "true")));

        mode.Should().BeOfType<InteractiveWebAssemblyRenderMode>();
    }

    /// <summary>
    /// A flag that is present but false selects nothing, the same as an absent one: the AppHost writes
    /// only <c>true</c>, but a hand-set <c>false</c> must not be read as "set".
    /// </summary>
    [Fact]
    public void FalseFlags_AreInteractiveAuto()
    {
        var mode = E2eRenderMode.Resolve(Configuration(
            (E2eRenderMode.ForceServerKey, "false"),
            (E2eRenderMode.ForceWebAssemblyKey, "false")));

        mode.Should().BeOfType<InteractiveAutoRenderMode>();
    }

    /// <summary>
    /// Both heads read the flags with <c>GetValue&lt;bool&gt;</c> today, which parses case-insensitively;
    /// moving the selector into Common keeps that, so a hand-set <c>TRUE</c> still selects.
    /// </summary>
    [Fact]
    public void FlagValue_IsCaseInsensitive()
    {
        var mode = E2eRenderMode.Resolve(Configuration((E2eRenderMode.ForceWebAssemblyKey, "TRUE")));

        mode.Should().BeOfType<InteractiveWebAssemblyRenderMode>();
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

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();
}
