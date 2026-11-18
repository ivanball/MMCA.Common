using System.Text.Json;
using AwesomeAssertions;

namespace MMCA.Common.Testing.E2E.Workflows.Pwa;

/// <summary>
/// The installability rules <see cref="InstallableWebAppTestsBase"/> applies to a host's parsed web
/// app manifest, kept apart from the browser so they are unit-tested without a running host: the
/// app's exact name and theme colour, a short name, standalone display, a root start URL, and icons
/// at 192 and 512 pixels with at least one maskable.
/// </summary>
internal static class InstallableManifest
{
    /// <summary>Asserts every installability rule on <paramref name="root"/>.</summary>
    /// <param name="root">The manifest's root JSON object.</param>
    /// <param name="expectedName">The exact manifest <c>name</c>.</param>
    /// <param name="expectedThemeColor">The exact manifest <c>theme_color</c>.</param>
    /// <returns>Each icon's <c>src</c>, for the caller to fetch.</returns>
    internal static IReadOnlyList<string> AssertInstallable(JsonElement root, string expectedName, string expectedThemeColor)
    {
        root.GetProperty("name").GetString().Should().Be(expectedName);
        root.GetProperty("short_name").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("display").GetString().Should().Be("standalone");
        root.GetProperty("start_url").GetString().Should().Be("/");
        root.GetProperty("theme_color").GetString().Should().Be(expectedThemeColor);

        var icons = root.GetProperty("icons").EnumerateArray().ToList();
        icons.Select(icon => icon.GetProperty("sizes").GetString()).Should().Contain("192x192").And.Contain("512x512");
        icons.Should().Contain(icon => string.Equals(icon.GetProperty("purpose").GetString(), "maskable", StringComparison.Ordinal));

        return [.. icons.Select(icon => icon.GetProperty("src").GetString() ?? string.Empty)];
    }
}
