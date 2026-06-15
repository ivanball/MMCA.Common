using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests.Infrastructure;

/// <summary>
/// Base for gallery E2E tests. Mirrors the shipped <c>E2ETestBase</c> per-test browser context/page
/// setup, but binds to the in-process <see cref="GalleryHostFixture"/> (two collection fixtures, which
/// the shipped base's single-fixture collection can't express). A Playwright trace is recorded per
/// test and written to <c>playwright-traces/</c> for CI failure diagnosis.
/// </summary>
[Collection(GalleryE2ECollection.Name)]
public abstract class GalleryAxeTestBase : IAsyncLifetime
{
    private readonly PlaywrightFixture _playwright;
    private readonly string _tracePath;
    private IBrowserContext _context = null!;

    protected IPage Page { get; private set; } = null!;

    protected GalleryAxeTestBase(PlaywrightFixture playwright, GalleryHostFixture gallery)
    {
        _playwright = playwright;
        BaseUrl = gallery.BaseUrl;

        var traceDir = Path.Combine(AppContext.BaseDirectory, "playwright-traces");
        Directory.CreateDirectory(traceDir);
        _tracePath = Path.Combine(traceDir, $"{GetType().Name}-{Path.GetRandomFileName()}.zip");
    }

    protected string BaseUrl { get; }

    public async ValueTask InitializeAsync()
    {
        _context = await _playwright.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = BaseUrl,
        });
        _context.SetDefaultTimeout(E2ETestConfiguration.DefaultTimeout);

        await _context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
        });

        Page = await _context.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        // Always persist the trace; CI uploads the folder only when the job fails.
        await _context.Tracing.StopAsync(new TracingStopOptions { Path = _tracePath });
        await Page.CloseAsync();
        await _context.DisposeAsync();
    }
}
