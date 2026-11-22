using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MMCA.Common.UI.Web.Hardening;
using MMCA.Common.UI.Web.Tests.Hosting;

namespace MMCA.Common.UI.Web.Tests.Hardening;

/// <summary>
/// Pins the UI host limiter's static-asset exemption (item B2) on the composed Blazor web
/// host and pipeline: a path with a file extension that maps to no static asset is metered by BOTH
/// the per-IP window and the concurrency ceiling, a real fingerprinted asset mapped by
/// <c>MapStaticAssets</c> is metered by neither, and the <c>/_blazor</c> negotiate stays inside the
/// per-IP window and outside the concurrency ceiling.
/// <para>
/// The asset is real: a file in a temporary web root, mapped through a static-asset manifest of the
/// same shape the Web SDK generates, so the request is served by the framework's static-asset
/// endpoint rather than by a test stand-in.
/// </para>
/// </summary>
public sealed class UiRateLimitingStaticAssetPipelineTests : IDisposable
{
    private const string AssetRoute = "/app.k3x9q2m7ab.css";
    private const string AssetContent = "body{color:#000}";
    private const string MissingAssetPath = "/definitely-missing.png";
    private const string NegotiatePath = "/_blazor/negotiate";
    private const string SlowPath = "/slow";
    private const string ProbePath = "/probe";

    // A unique application name per test, so the manifest the composed pipeline's own
    // MapStaticAssets() reads ({ApplicationName}.staticwebassets.endpoints.json) is this test's alone
    // and never the empty one the shared harness writes for the test assembly's name.
    private readonly string _applicationName = $"MMCA.Common.UI.Web.Tests.StaticAssets.{Guid.NewGuid():N}";
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "mmca-ui-web-tests", $"webroot-{Guid.NewGuid():N}");
    private readonly string _manifestPath;

    public UiRateLimitingStaticAssetPipelineTests()
    {
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "app.css"), AssetContent);
        _manifestPath = Path.Combine(AppContext.BaseDirectory, $"{_applicationName}.staticwebassets.endpoints.json");
        File.WriteAllText(_manifestPath, Manifest());
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_manifestPath);
            Directory.Delete(_webRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp folder left behind does not affect another test.
        }
    }

    [Fact]
    public async Task MissingAssetPath_IsCountedByThePerIpWindow_AndEventuallyGets429()
    {
        var limits = new Limits(PermitLimit: 3);
        await using var app = await StartAsync(limits);
        var server = app.GetTestServer();

        var statuses = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add(await SendAsync(server, HttpMethods.Get, MissingAssetPath, "203.0.113.10"));
        }

        statuses.Take(3).Should().NotContain(StatusCodes.Status429TooManyRequests, "the window holds three permits");
        statuses[3].Should().Be(
            StatusCodes.Status429TooManyRequests,
            "a file extension that maps to no static asset is a page-cost request and must be counted");
    }

    [Fact]
    public async Task RealFingerprintedStaticAsset_IsNotCountedByThePerIpWindow()
    {
        var limits = new Limits(PermitLimit: 3);
        await using var app = await StartAsync(limits);
        var server = app.GetTestServer();
        const string ip = "203.0.113.11";

        // Control: the asset endpoint really serves the file.
        using var client = app.GetTestClient();
        using var assetResponse = await client.GetAsync(new Uri(AssetRoute, UriKind.Relative), TestContext.Current.CancellationToken);
        (await assetResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be(AssetContent);

        var assetStatuses = new List<int>();
        for (var i = 0; i < 10; i++)
        {
            assetStatuses.Add(await SendAsync(server, HttpMethods.Get, AssetRoute, ip));
        }

        var pageStatuses = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            pageStatuses.Add(await SendAsync(server, HttpMethods.Get, ProbePath, ip));
        }

        assetStatuses.Should().OnlyContain(status => status == StatusCodes.Status200OK, "a real static asset is never throttled");
        pageStatuses.Take(3).Should().OnlyContain(
            status => status == StatusCodes.Status200OK,
            "ten asset requests must not have consumed any of the caller's three permits");
        pageStatuses[3].Should().Be(StatusCodes.Status429TooManyRequests, "the window is live for this caller");
    }

    [Fact]
    public async Task WhileTheConcurrencyCeilingIsFull_AMissingAssetIsRefused_ButARealAssetAndTheNegotiateAreNot()
    {
        var limits = new Limits(PermitLimit: 100, GlobalConcurrencyLimit: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartAsync(limits, entered, release);
        var server = app.GetTestServer();

        var held = SendAsync(server, HttpMethods.Get, SlowPath, "203.0.113.20");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        try
        {
            var page = await SendAsync(server, HttpMethods.Get, ProbePath, "203.0.113.21");
            var missing = await SendAsync(server, HttpMethods.Get, MissingAssetPath, "203.0.113.22");
            var asset = await SendAsync(server, HttpMethods.Get, AssetRoute, "203.0.113.23");
            var negotiate = await SendAsync(server, HttpMethods.Post, NegotiatePath, "203.0.113.24");

            page.Should().Be(StatusCodes.Status429TooManyRequests, "control: the single concurrency permit is held");
            missing.Should().Be(
                StatusCodes.Status429TooManyRequests,
                "a path with an extension that maps to no asset is metered by the concurrency ceiling");
            asset.Should().Be(StatusCodes.Status200OK, "a real static asset holds no concurrency permit");
            negotiate.Should().NotBe(
                StatusCodes.Status429TooManyRequests,
                "the Blazor transport is kept out of the concurrency ceiling");
        }
        finally
        {
            release.TrySetResult();
        }

        (await held).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task BlazorNegotiate_StaysInThePerIpWindow()
    {
        var limits = new Limits(PermitLimit: 2);
        await using var app = await StartAsync(limits);
        var server = app.GetTestServer();

        var statuses = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add(await SendAsync(server, HttpMethods.Post, NegotiatePath, "203.0.113.30"));
        }

        statuses.Take(2).Should().NotContain(StatusCodes.Status429TooManyRequests);
        statuses[2].Should().Be(StatusCodes.Status429TooManyRequests, "the negotiate opens a circuit and is counted per IP");
    }

    private Task<WebApplication> StartAsync(
        Limits limits,
        TaskCompletionSource? entered = null,
        TaskCompletionSource? release = null)
    {
        // The shared harness's CreateBuilder, with the web root and application name set at
        // creation (WebApplicationBuilder refuses to change either afterwards).
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ApplicationName = _applicationName,
            WebRootPath = _webRoot,
        });
        builder.WebHost.UseTestServer();
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = false;
            options.ValidateScopes = false;
        });
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Api:ApiEndpoint"] = BlazorWebHostHarness.ApiEndpoint,
                ["Api:WasmApiEndpoint"] = BlazorWebHostHarness.WasmApiEndpoint,
                [$"{UiRateLimitingSettings.SectionName}:Enabled"] = "true",
                [$"{UiRateLimitingSettings.SectionName}:PermitLimit"] = limits.PermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [$"{UiRateLimitingSettings.SectionName}:WindowSeconds"] = "60",
                [$"{UiRateLimitingSettings.SectionName}:GlobalConcurrencyLimit"] =
                    limits.GlobalConcurrencyLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        return BlazorWebHostHarness.StartAsync(
            builder,
            afterPipeline: web =>
            {
                web.MapGet(ProbePath, () => "ok").AllowAnonymous();
                web.MapGet(SlowPath, async () =>
                {
                    entered?.TrySetResult();
                    if (release is not null)
                    {
                        await release.Task;
                    }

                    return "done";
                }).AllowAnonymous();
            });
    }

    private static async Task<int> SendAsync(TestServer server, string method, string path, string clientIp)
    {
        var context = await server.SendAsync(
            request =>
            {
                request.Request.Method = method;
                request.Request.Path = path;
                request.Connection.RemoteIpAddress = IPAddress.Parse(clientIp);
            },
            TestContext.Current.CancellationToken);

        return context.Response.StatusCode;
    }

    private static string Manifest() =>
        "{\"Version\":1,\"ManifestType\":\"Build\",\"Endpoints\":[{"
        + $"\"Route\":\"{AssetRoute.TrimStart('/')}\",\"AssetFile\":\"app.css\",\"Selectors\":[],"
        + "\"ResponseHeaders\":["
        + "{\"Name\":\"Cache-Control\",\"Value\":\"max-age=31536000, immutable\"},"
        + $"{{\"Name\":\"Content-Length\",\"Value\":\"{AssetContent.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"}},"
        + "{\"Name\":\"Content-Type\",\"Value\":\"text/css\"},"
        + "{\"Name\":\"ETag\",\"Value\":\"\\\"k3x9q2m7abTestEtag=\\\"\"},"
        + "{\"Name\":\"Last-Modified\",\"Value\":\"Thu, 19 Nov 2026 03:25:08 GMT\"}],"
        + "\"EndpointProperties\":[{\"Name\":\"fingerprint\",\"Value\":\"k3x9q2m7ab\"},{\"Name\":\"label\",\"Value\":\"app.css\"}]}]}";

    private sealed record Limits(int PermitLimit, int GlobalConcurrencyLimit = 200);
}
