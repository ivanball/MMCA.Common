using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using MMCA.Common.Testing.E2E.Workflows.Pwa;

namespace MMCA.Common.Testing.E2E.Tests.Workflows;

/// <summary>
/// The installability rules <see cref="InstallableWebAppTestsBase"/> applies to a host's manifest,
/// run against manifest JSON directly: a valid manifest passes and hands back its icon sources, and
/// each rule fails on a manifest that breaks only that rule.
/// </summary>
public sealed class InstallableManifestTests
{
    private const string Name = "Sample App";
    private const string ThemeColor = "#0D7377";

    /// <summary>A manifest meeting every rule passes and returns each icon's src in order.</summary>
    [Fact]
    public void AValidManifest_PassesAndReturnsTheIconSources()
    {
        var sources = Assert(ValidManifest());

        sources.Should().Equal("/icon-192.png", "/icon-512.png", "/icon-maskable.png");
    }

    /// <summary>Each rule, broken alone on an otherwise valid manifest, fails the assertion.</summary>
    /// <param name="property">The manifest property to replace.</param>
    /// <param name="value">The breaking value, as JSON.</param>
    [Theory]
    [InlineData("name", "\"Other App\"")]
    [InlineData("short_name", "\" \"")]
    [InlineData("display", "\"browser\"")]
    [InlineData("start_url", "\"/home\"")]
    [InlineData("theme_color", "\"#FFFFFF\"")]
    [InlineData("icons", "[{\"src\":\"/a.png\",\"sizes\":\"192x192\",\"purpose\":\"maskable\"}]")]
    [InlineData("icons", "[{\"src\":\"/a.png\",\"sizes\":\"192x192\",\"purpose\":\"any\"},{\"src\":\"/b.png\",\"sizes\":\"512x512\",\"purpose\":\"any\"}]")]
    public void AManifestBreakingOneRule_Fails(string property, string value)
    {
        var manifest = ValidManifest();
        manifest[property] = JsonNode.Parse(value);

        var act = () => Assert(manifest);

        act.Should().Throw<Exception>();
    }

    private static IReadOnlyList<string> Assert(JsonObject manifest)
    {
        using var document = JsonDocument.Parse(manifest.ToJsonString());
        return InstallableManifest.AssertInstallable(document.RootElement, Name, ThemeColor);
    }

    private static JsonObject ValidManifest() => new()
    {
        ["name"] = Name,
        ["short_name"] = "Sample",
        ["display"] = "standalone",
        ["start_url"] = "/",
        ["theme_color"] = ThemeColor,
        ["icons"] = new JsonArray(
            new JsonObject { ["src"] = "/icon-192.png", ["sizes"] = "192x192", ["purpose"] = "any" },
            new JsonObject { ["src"] = "/icon-512.png", ["sizes"] = "512x512", ["purpose"] = "any" },
            new JsonObject { ["src"] = "/icon-maskable.png", ["sizes"] = "512x512", ["purpose"] = "maskable" }),
    };
}
