using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Testing.E2E.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.Testing.E2E.Workflows.Pwa;

/// <summary>
/// The installable-web-app surface (ADR-136) as a consumer host serves it: the home page links a
/// manifest that an installing browser accepts (the app's name, standalone display, root start URL,
/// the app's theme colour, 192, 512 and maskable icons that all resolve as PNG, and a matching
/// <c>theme-color</c> meta tag), and the framework service worker is served at the site root as
/// uncached JavaScript that knows the framework offline page. Registration itself is proven by
/// MMCA.Common's gallery suite; this pins what each host supplies and maps.
/// </summary>
/// <remarks>
/// A consumer keeps a small sealed subclass (the ADR-015 pattern) carrying its xUnit
/// <c>[Collection]</c> and naming <see cref="ExpectedName"/> and <see cref="ExpectedThemeColor"/>.
/// Neither fact signs in, so a host that accepts terms after sign-in needs no override here.
/// </remarks>
public abstract class InstallableWebAppTestsBase : E2ETestBase
{
    protected InstallableWebAppTestsBase(PlaywrightFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>Gets the manifest <c>name</c> the host ships, compared exactly.</summary>
    protected abstract string ExpectedName { get; }

    /// <summary>
    /// Gets the manifest <c>theme_color</c> the host ships (for example <c>#0D7377</c>), compared
    /// exactly against both the manifest and the page's <c>theme-color</c> meta tag.
    /// </summary>
    protected abstract string ExpectedThemeColor { get; }

    [Fact]
    public async Task Home_LinksAnInstallableManifest_WhoseIconsAllResolve()
    {
        await NavigateAndWaitAsync("/").ConfigureAwait(false);

        var href = await Page.Locator("link[rel='manifest']").GetAttributeAsync("href").ConfigureAwait(false) ?? string.Empty;
        href.Should().NotBeEmpty();
        var manifestUrl = new Uri(new Uri(Page.Url), href);

        var response = await Page.APIRequest.GetAsync(manifestUrl.AbsoluteUri).ConfigureAwait(false);
        response.Status.Should().Be(200);
        using var manifest = JsonDocument.Parse(await response.TextAsync().ConfigureAwait(false));
        var root = manifest.RootElement;

        root.GetProperty("name").GetString().Should().Be(ExpectedName);
        root.GetProperty("short_name").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("display").GetString().Should().Be("standalone");
        root.GetProperty("start_url").GetString().Should().Be("/");
        root.GetProperty("theme_color").GetString().Should().Be(ExpectedThemeColor);

        var icons = root.GetProperty("icons").EnumerateArray().ToList();
        icons.Select(icon => icon.GetProperty("sizes").GetString()).Should().Contain("192x192").And.Contain("512x512");
        icons.Should().Contain(icon => string.Equals(icon.GetProperty("purpose").GetString(), "maskable", StringComparison.Ordinal));
        foreach (var icon in icons)
        {
            var iconUrl = new Uri(manifestUrl, icon.GetProperty("src").GetString() ?? string.Empty);
            var iconResponse = await Page.APIRequest.GetAsync(iconUrl.AbsoluteUri).ConfigureAwait(false);
            iconResponse.Status.Should().Be(200, because: $"{iconUrl} is a manifest icon");
            iconResponse.Headers["content-type"].Should().StartWith("image/png");
        }

        await Expect(Page.Locator("meta[name='theme-color']")).ToHaveAttributeAsync("content", ExpectedThemeColor).ConfigureAwait(false);
    }

    [Fact]
    public async Task TheServiceWorker_IsServedAtTheSiteRoot_AsUncachedJavaScript()
    {
        await NavigateAndWaitAsync("/").ConfigureAwait(false);

        var response = await Page.APIRequest.GetAsync(new Uri(new Uri(Page.Url), "/service-worker.js").AbsoluteUri).ConfigureAwait(false);

        response.Status.Should().Be(200);
        response.Headers["content-type"].Should().StartWith("text/javascript");
        response.Headers["cache-control"].Should().Be("no-cache");
        (await response.TextAsync().ConfigureAwait(false)).Should().Contain("/_content/MMCA.Common.UI.Web/offline.html");
    }
}
