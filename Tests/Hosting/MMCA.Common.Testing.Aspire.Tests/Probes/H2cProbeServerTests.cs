using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using MMCA.Common.Testing.Aspire.Probes;

namespace MMCA.Common.Testing.Aspire.Tests.Probes;

/// <summary>
/// Exercises <see cref="H2cProbe"/> against a REAL cleartext Kestrel listener configured
/// <see cref="HttpProtocols.Http2"/>, which is the profile every extracted MMCA service runs and the
/// only one that serves h2c.
/// <para>
/// This lives here, next to a listener the test owns, rather than in the AppHost tier, because the
/// AppHost tier cannot answer the question: Aspire fronts a project resource with its own endpoint
/// proxy, so what a client observes there is the proxy's protocol handling and not the service's.
/// A plain listener on loopback is the only place the service-side contract is directly observable.
/// </para>
/// </summary>
public sealed class H2cProbeServerTests : IAsyncLifetime
{
    private WebApplication? _app;
    private Uri _endpoint = new("http://127.0.0.1");

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // Loopback with an explicit address, not ListenLocalhost: Kestrel refuses dynamic port
        // binding on "localhost" because it would have to bind both loopback families at once.
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, endpoint => endpoint.Protocols = HttpProtocols.Http2));

        var app = builder.Build();
        app.MapGet(AppHostProbePaths.Alive, () => "Healthy");

        await app.StartAsync().ConfigureAwait(false);
        _app = app;

        _endpoint = new Uri(app.Urls.First(), UriKind.Absolute);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }

    [Fact]
    public async Task SendAsync_NegotiatesHttp2_AgainstAnHttp2OnlyCleartextListener()
    {
        using var response = await H2cProbe
            .SendAsync(new Uri(_endpoint, AppHostProbePaths.Alive), TestContext.Current.CancellationToken);

        response.Version.Major.Should().Be(2, "prior knowledge is the whole point of the probe");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnExactHttp11Request_DoesNotSucceed_AgainstThatListener()
    {
        // The negative control that keeps the assertion above from being vacuous: if the listener
        // also served HTTP/1.1, proving it answered HTTP/2 would prove nothing about the h2c
        // contract. Kestrel may refuse at the connection level or answer a non-success status; both
        // are a refusal, and "answers 200 over HTTP/1.1" is the only outcome that would matter.
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, AppHostProbePaths.Alive))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        HttpStatusCode? status = null;
        try
        {
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            status = response.StatusCode;
        }
        catch (HttpRequestException)
        {
            // Refused at the connection level, which is the strongest form of the refusal.
        }

        status.Should().NotBe(
            HttpStatusCode.OK,
            "an Http2-only cleartext endpoint must not serve a real HTTP/1.1 request");
    }
}
