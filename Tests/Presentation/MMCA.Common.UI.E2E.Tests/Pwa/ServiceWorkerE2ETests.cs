using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Pwa;

/// <summary>
/// The installable-web-app worker in a real browser: <c>pwa-register.js</c> registers
/// <c>/service-worker.js</c> at the site root and the worker takes control of the page, and a
/// navigation that cannot reach the network renders the framework offline page instead of the
/// browser's error page. The gallery maps the worker exactly as a consumer host does.
/// </summary>
public sealed class ServiceWorkerE2ETests : GalleryAxeTestBase
{
    public ServiceWorkerE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task TheWorker_RegistersAtTheSiteRoot_AndControlsThePage()
    {
        await Page.GotoAndWaitForBlazorAsync("/login");

        var scope = await Page.EvaluateAsync<string>("async () => (await navigator.serviceWorker.ready).scope");
        await Page.WaitForFunctionAsync("() => navigator.serviceWorker.controller !== null");

        Assert.Equal(new Uri(new Uri(BaseUrl), "/").AbsoluteUri, scope);
    }

    [Fact]
    public async Task AnOfflineNavigation_RendersTheOfflinePage()
    {
        // Playwright's offline emulation reaches a service worker's own fetches only on Chromium;
        // on the other engines the worker would still see the network, so the assertion is moot there.
        Assert.SkipUnless(
            string.Equals(E2ETestConfiguration.Browser, "chromium", StringComparison.OrdinalIgnoreCase),
            "Offline emulation does not reach a service worker's fetches on this engine.");

        await Page.GotoAndWaitForBlazorAsync("/login");
        await Page.WaitForFunctionAsync("() => navigator.serviceWorker.controller !== null");

        await Page.Context.SetOfflineAsync(true);
        try
        {
            await Page.GotoAsync("/components");

            await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "You are offline" })).ToBeVisibleAsync();
            await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "Try again" })).ToBeVisibleAsync();
        }
        finally
        {
            await Page.Context.SetOfflineAsync(false);
        }
    }
}
