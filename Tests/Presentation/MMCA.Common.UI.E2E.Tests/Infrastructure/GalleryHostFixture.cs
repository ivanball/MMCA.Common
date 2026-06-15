using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.Gallery;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests.Infrastructure;

/// <summary>
/// Collection fixture that self-hosts the gallery in-process on an ephemeral Kestrel port and exposes
/// its base URL. Hosting in-process (a real <c>StartAsync()</c> on a bound port — not
/// <c>WebApplicationFactory</c>'s in-memory TestServer, which Playwright cannot reach over the wire)
/// avoids the separate <c>dotnet run</c> + health-poll that made ADC's e2e.yml cold-start fragile.
/// </summary>
public sealed class GalleryHostFixture : IAsyncLifetime
{
    private WebApplication? _app;

    /// <summary>The bound <c>http://127.0.0.1:{port}</c> base URL of the running gallery.</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _app = GalleryHost.BuildApp([]);

        // Port 0 → Kestrel binds an ephemeral free port (no fixed-port clashes in CI).
        _app.Urls.Clear();
        _app.Urls.Add("http://127.0.0.1:0");

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses;
        BaseUrl = addresses.First();

        // Belt-and-suspenders: also point the shipped E2E config at the self-hosted gallery, in case
        // any reused helper reads E2ETestConfiguration.BaseUrl rather than the per-context BaseURL.
        E2ETestConfiguration.DefaultBaseUrl = BaseUrl;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
