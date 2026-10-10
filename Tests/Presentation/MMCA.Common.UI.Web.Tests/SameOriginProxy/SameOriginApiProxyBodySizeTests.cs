using System.Collections.Concurrent;
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.API;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Web.SameOriginProxy;

namespace MMCA.Common.UI.Web.Tests.SameOriginProxy;

/// <summary>
/// S-10 (local test run 10): uploads of 31-50 MB fail in WebAssembly mode because the same-origin proxy
/// keeps Kestrel's default <c>MaxRequestBodySize</c> (30,000,000 bytes) and answers a bare 413 before
/// forwarding. Planned design: a <c>SameOriginApiProxy:MaxRequestBodySizeByPath</c> setting, a
/// dictionary from a GATEWAY-ROOT-RELATIVE path (same shape as <c>RefreshPath</c> and
/// <c>AdditionalTokenIssuingPaths</c>, for example <c>uploads/file</c>, case-insensitive) to a byte limit.
/// A request whose remaining path equals that path or starts with it as whole segments (like
/// <c>PathString.StartsWithSegments</c>) gets <c>IHttpMaxRequestBodySizeFeature.MaxRequestBodySize</c>
/// raised to that limit; every other path keeps the server default; a non-positive limit fails startup
/// validation.
/// <para>
/// The setting is configured through configuration KEYS only (never the C# member), so this file
/// compiles before the property exists. The UI host runs on real Kestrel at port 0 because TestServer
/// implements no <c>IHttpMaxRequestBodySizeFeature</c> and so cannot observe the 413 at all; the fake
/// gateway behind it stays in-process and drains and counts every forwarded body.
/// </para>
/// </summary>
public sealed class SameOriginApiProxyBodySizeTests : IAsyncLifetime
{
    private const long FortyMebibytes = 40L * 1024 * 1024;

    // Just over Kestrel's default MaxRequestBodySize of 30,000,000 bytes.
    private const long OverDefaultLimit = 31_000_000;

    private const string LimitKey = "SameOriginApiProxy:MaxRequestBodySizeByPath:uploads/file";

    private IHost _gateway = null!;

    private ConcurrentQueue<(string Path, long Bytes)> ReceivedBodies { get; } = new();

    public async ValueTask InitializeAsync()
    {
        _gateway = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app => app.Run(async context =>
                {
                    var buffer = new byte[81_920];
                    long total = 0;
                    int read;
                    while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                    {
                        total += read;
                    }

                    ReceivedBodies.Enqueue((context.Request.Path.Value ?? string.Empty, total));
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    await context.Response.WriteAsJsonAsync(new { received = total }, context.RequestAborted);
                })))
            .Build();
        await _gateway.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _gateway.StopAsync();
        _gateway.Dispose();
    }

    [Fact]
    public async Task ConfiguredUploadPath_AcceptsABodyOverTheDefaultCap_AndForwardsEveryByte()
    {
        // Defect S-10: today the setting does not exist, the default 30,000,000-byte cap applies and
        // Kestrel answers 413 before a single byte is forwarded.
        using var host = await StartKestrelProxyHostAsync(new Dictionary<string, string?>
        {
            [LimitKey] = FortyMebibytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        using var client = CreateClient(host);
        using var request = UploadRequest("/api/uploads/file", OverDefaultLimit);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "S-10: a path listed in SameOriginApiProxy:MaxRequestBodySizeByPath with a 40 MiB limit must accept a "
            + "31,000,000-byte body instead of Kestrel's default-cap 413");
        ReceivedBodies.Should().ContainSingle().Which.Should().Be(("/uploads/file", OverDefaultLimit));
    }

    [Fact]
    public async Task UnlistedPath_KeepsTheDefaultCap_AndIsRefused413()
    {
        // Regression guard for the S-10 fix: raising the cap for one listed path must not raise it anywhere else.
        using var host = await StartKestrelProxyHostAsync(new Dictionary<string, string?>
        {
            [LimitKey] = FortyMebibytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        using var client = CreateClient(host);
        using var request = UploadRequest("/api/other/file", OverDefaultLimit);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        ReceivedBodies.Should().BeEmpty("the body is refused before anything reaches the gateway");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task NonPositiveConfiguredLimit_FailsTheBoot(string limit)
    {
        // Defect S-10 (validation half): today the key binds to nothing and the boot succeeds.
        var act = async () =>
        {
            using var host = await StartKestrelProxyHostAsync(new Dictionary<string, string?> { [LimitKey] = limit });
        };

        await act.Should().ThrowAsync<OptionsValidationException>(
            "S-10: a MaxRequestBodySizeByPath limit must be a positive byte count, validated on start like the other proxy settings");
    }

    /// <summary>
    /// The UI host as <see cref="ProxyHost"/> builds it (session cookies plus the opted-in proxy), but on
    /// real Kestrel at a loopback port chosen by the OS, so <c>MaxRequestBodySize</c> is enforced.
    /// </summary>
    private async Task<IHost> StartKestrelProxyHostAsync(IDictionary<string, string?> configuration)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Api:ApiEndpoint"] = "http://gateway",
            ["Api:WasmApiEndpoint"] = ProxyHost.WasmApiEndpoint,
        };
        foreach (var (key, value) in configuration)
        {
            settings[key] = value;
        }

        var gatewayServer = _gateway.GetTestServer();
        var host = new HostBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings))
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0))
                .UseEnvironment(Environments.Production)
                .ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.Configure<ApiSettings>(context.Configuration.GetSection(ApiSettings.SectionName));
                    services.AddServerAuthSessionCookie("http://gateway/");
                    services.AddHttpClient("SessionCookieRefreshClient").ConfigurePrimaryHttpMessageHandler(() => gatewayServer.CreateHandler());
                    services.AddCommonSameOriginApiProxy(context.Configuration);
                    services.Replace(ServiceDescriptor.Singleton(new SameOriginProxyInvoker(gatewayServer.CreateHandler())));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapCommonSameOriginApiProxy();
                        endpoints.MapSessionCookieEndpoints();
                    });
                }))
            .Build();

        try
        {
            await host.StartAsync();
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static HttpClient CreateClient(IHost host)
    {
        var address = host.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        return new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>
    /// A same-origin upload as the browser sends it through the proxy (CSRF header, no foreign Origin),
    /// streamed from a non-allocating source. <c>Expect: 100-continue</c> lets the server refuse on the
    /// declared length before the body is sent, so a 413 is read as a response rather than surfacing as
    /// a connection reset mid-upload.
    /// </summary>
    private static HttpRequestMessage UploadRequest(string path, long length)
    {
        var request = ProxyHost.Request(HttpMethod.Post, path, csrf: true);
        request.Headers.ExpectContinue = true;
        request.Content = new StreamContent(new ZeroStream(length), 81_920);
        request.Content.Headers.ContentLength = length;
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        return request;
    }

    /// <summary>A forward-only stream of <c>length</c> zero bytes that never holds more than one buffer.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var take = (int)Math.Min(buffer.Length, _remaining);
            buffer[..take].Clear();
            _remaining -= take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
