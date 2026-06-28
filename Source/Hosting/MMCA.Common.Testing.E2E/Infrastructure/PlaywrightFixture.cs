using Microsoft.Playwright;
using Xunit;

namespace MMCA.Common.Testing.E2E.Infrastructure;

public sealed class PlaywrightFixture : IAsyncLifetime
{
    // One captured authenticated session (storageState JSON: cookies + localStorage) per role key, shared
    // by the whole collection. Lets every test reuse a single login per role instead of authenticating per
    // test (the dominant resource-contention timeout on a 2-core CI runner — TD-06/07).
    private readonly Dictionary<string, string> _storageStateByRole = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _stateLock = new(1, 1);

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

    /// <summary>
    /// Returns a cached authenticated <c>storageState</c> for <paramref name="roleKey"/>, performing a
    /// single real UI login (via <paramref name="performLogin"/>) the first time and reusing the captured
    /// cookies + localStorage thereafter. This lets the whole collection share one session per role rather
    /// than logging in per test, removing the per-test auth round-trip that dominates CI-runner contention.
    /// Tests run sequentially within the collection; the lock just guards the lazy init defensively.
    /// </summary>
    public async Task<string> GetAuthenticatedStorageStateAsync(string roleKey, Func<IPage, Task> performLogin)
    {
        ArgumentNullException.ThrowIfNull(performLogin);

        await _stateLock.WaitAsync();
        try
        {
            if (_storageStateByRole.TryGetValue(roleKey, out var cached))
            {
                return cached;
            }

            var context = await Browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                BaseURL = E2ETestConfiguration.BaseUrl,
            });
            context.SetDefaultTimeout(E2ETestConfiguration.DefaultTimeout);
            try
            {
                var page = await context.NewPageAsync();
                await performLogin(page);
                var state = await context.StorageStateAsync();
                _storageStateByRole[roleKey] = state;
                return state;
            }
            finally
            {
                await context.DisposeAsync();
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        _stateLock.Dispose();
        await Browser.DisposeAsync();
        Playwright.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class E2ETestCollection : ICollectionFixture<PlaywrightFixture>
{
    public const string Name = "E2E";
}
