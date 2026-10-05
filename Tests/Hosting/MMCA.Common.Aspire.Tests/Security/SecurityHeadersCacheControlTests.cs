using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Security;

namespace MMCA.Common.Aspire.Tests.Security;

/// <summary>
/// X-15 (Store run 1): a credential page (<c>/reset-password</c>, <c>/auth/oauth-complete</c>) ended
/// up with <c>Cache-Control: no-cache, no-store, max-age=0</c>, because
/// <see cref="SecurityHeadersMiddleware"/> wrote its value BEFORE calling the rest of the pipeline and
/// a downstream component (the Razor Components endpoint) overwrote it. The final response must carry
/// the middleware's full value, <c>no-store, no-cache, must-revalidate, max-age=0</c>, whatever the
/// pipeline wrote in between: the value is settled when the response starts, so nothing downstream can
/// weaken it.
/// </summary>
public sealed class SecurityHeadersCacheControlTests
{
    private const string CredentialCacheControl = "no-store, no-cache, must-revalidate, max-age=0";

    // What the Razor Components endpoint writes for a page render.
    private const string DownstreamCacheControl = "no-cache, no-store, max-age=0";

    [Theory]
    [InlineData("/reset-password")]
    [InlineData("/auth/oauth-complete")]
    public async Task CredentialPath_DownstreamOverwriteDuringTheRequest_FinalResponseKeepsTheFullValue(string path)
    {
        var (context, responseFeature) = CreateContext(path);
        var middleware = CreateMiddleware(ctx =>
        {
            ctx.Response.Headers.CacheControl = DownstreamCacheControl;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);
        await responseFeature.StartAsync();

        context.Response.Headers.CacheControl.ToString().Should().Be(
            CredentialCacheControl,
            "a page a credential arrives on must keep must-revalidate and the full no-store value even after a downstream component wrote its own");
    }

    [Fact]
    public async Task CredentialPath_DownstreamOverwriteAtResponseStart_FinalResponseKeepsTheFullValue()
    {
        var (context, responseFeature) = CreateContext("/reset-password");
        var middleware = CreateMiddleware(ctx =>
        {
            // A component that registers its own OnStarting callback runs it before any callback the
            // middleware registered earlier (response-start callbacks run last-registered first).
            ctx.Response.OnStarting(() =>
            {
                ctx.Response.Headers.CacheControl = DownstreamCacheControl;
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);
        await responseFeature.StartAsync();

        context.Response.Headers.CacheControl.ToString().Should().Be(CredentialCacheControl);
    }

    [Fact]
    public async Task OrdinaryPath_DownstreamValueIsLeftAlone()
    {
        var (context, responseFeature) = CreateContext("/products");
        var middleware = CreateMiddleware(ctx =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=60";
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);
        await responseFeature.StartAsync();

        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=60", "only credential pages are forced to no-store");
    }

    private static SecurityHeadersMiddleware CreateMiddleware(RequestDelegate next) =>
        new(
            next,
            Options.Create(new SecurityHeadersSettings()),
            new NoCspProvider(),
            new StubWebHostEnvironment(Environments.Production));

    private static (DefaultHttpContext Context, StartableResponseFeature ResponseFeature) CreateContext(string path)
    {
        var responseFeature = new StartableResponseFeature();
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature { Path = path, Method = HttpMethods.Get });
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Stream.Null));

        return (new DefaultHttpContext(features), responseFeature);
    }

    /// <summary>
    /// A response feature that records <c>OnStarting</c> callbacks and runs them, last registered
    /// first, when the test starts the response (the order Kestrel and TestServer use).
    /// </summary>
    private sealed class StartableResponseFeature : HttpResponseFeature
    {
        private readonly Stack<(Func<object, Task> Callback, object State)> _onStarting = new();
        private bool _started;

        public override bool HasStarted => _started;

        public override void OnStarting(Func<object, Task> callback, object state) => _onStarting.Push((callback, state));

        public async Task StartAsync()
        {
            while (_onStarting.Count > 0)
            {
                var (callback, state) = _onStarting.Pop();
                await callback(state);
            }

            _started = true;
        }
    }

    private sealed class NoCspProvider : ICspPolicyProvider
    {
        public CspPolicy? GetPolicy(HttpContext context) => null;
    }

    private sealed class StubWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
