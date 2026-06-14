using Microsoft.Playwright;
using Xunit;

namespace MMCA.Common.Testing.E2E.Infrastructure;

public sealed class PlaywrightFixture : IAsyncLifetime
{
    public IPlaywright Playwright { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();

        // Cross-browser matrix (rubric §22): the engine is env-selected so CI can run the same
        // suite against Chromium, Firefox, and WebKit. Unknown values fall back to Chromium.
        var browserType = E2ETestConfiguration.Browser.ToUpperInvariant() switch
        {
            "FIREFOX" => Playwright.Firefox,
            "WEBKIT" => Playwright.Webkit,
            _ => Playwright.Chromium,
        };

        Browser = await browserType.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = E2ETestConfiguration.Headless,
            SlowMo = E2ETestConfiguration.SlowMo,
        });
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await Browser.DisposeAsync();
        Playwright.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class E2ETestCollection : ICollectionFixture<PlaywrightFixture>
{
    public const string Name = "E2E";
}
