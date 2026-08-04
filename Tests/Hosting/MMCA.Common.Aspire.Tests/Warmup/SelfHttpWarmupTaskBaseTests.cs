using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Aspire.Warmup;

namespace MMCA.Common.Aspire.Tests.Warmup;

/// <summary>
/// The shared self-HTTP warm-up base (ADR-025): once the server is listening, replay a service's hot
/// read paths against its own endpoint. The behaviors asserted here are the ones the per-service
/// copies each had to get right on their own: port resolution under dynamic ports, the Testing
/// short-circuit, the h2c version pin, per-service success semantics, and the non-fatal wrapper that
/// keeps a failed warm-up from taking the replica down with it.
/// </summary>
public sealed class SelfHttpWarmupTaskBaseTests
{
    private static IConfiguration Config(string? aspnetcoreUrls = null)
    {
        var values = new Dictionary<string, string?>();
        if (aspnetcoreUrls is not null)
        {
            values["ASPNETCORE_URLS"] = aspnetcoreUrls;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static FakeServer ServerWith(params string[] addresses)
    {
        var addressFeature = new ServerAddressesFeature();
        foreach (var address in addresses)
        {
            addressFeature.Addresses.Add(address);
        }

        var features = new FeatureCollection();
        features.Set<IServerAddressesFeature>(addressFeature);
        return new FakeServer(features);
    }

    private static FakeServer ServerWithoutAddressesFeature() => new(new FeatureCollection());

    /// <summary>A port nothing is listening on: bound then released, so a connect is refused at once.</summary>
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ---------------------------------------------------------------- port resolution
    [Fact]
    public void ResolveWarmupPort_PrefersTheServersBoundCleartextAddress()
    {
        var port = SelfHttpWarmupTaskBase.ResolveWarmupPort(
            ServerWith("http://localhost:51234"),
            Config("http://+:8080"));

        port.Should().Be(51234, "the bound address is the only correct answer under Aspire's dynamic ports");
    }

    [Fact]
    public void ResolveWarmupPort_SkipsHttpsAddresses()
    {
        var port = SelfHttpWarmupTaskBase.ResolveWarmupPort(
            ServerWith("https://localhost:7001", "http://localhost:51234"),
            Config());

        port.Should().Be(51234, "the warm-up self-requests over cleartext");
    }

    [Fact]
    public void ResolveWarmupPort_FallsBackToAspnetcoreUrls_WhenTheServerHasNoCleartextAddress()
    {
        var port = SelfHttpWarmupTaskBase.ResolveWarmupPort(
            ServerWith("https://localhost:7001"),
            Config("http://+:8080"));

        port.Should().Be(8080);
    }

    [Fact]
    public void ResolveWarmupPort_FallsBackToAspnetcoreUrls_WhenThereIsNoAddressesFeature()
    {
        var port = SelfHttpWarmupTaskBase.ResolveWarmupPort(
            ServerWithoutAddressesFeature(),
            Config("http://+:5005"));

        port.Should().Be(5005);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://+:not-a-port")]
    [InlineData("http://localhost:8080/")]
    public void ResolveWarmupPort_FallsBackToTheContainerDefault_WhenNothingParses(string? aspnetcoreUrls)
    {
        var port = SelfHttpWarmupTaskBase.ResolveWarmupPort(ServerWith(), Config(aspnetcoreUrls));

        port.Should().Be(SelfHttpWarmupTaskBase.DefaultPort);
    }

    // ---------------------------------------------------------------- execution semantics
    [Fact]
    public async Task TestingEnvironment_SkipsTheWarmupWithoutWaitingForTheServer()
    {
        using var lifetime = new FakeLifetime();
        var logger = new CapturingLogger();
        var task = new ConfigurableWarmupTask(
            ServerWith("http://localhost:8080"),
            Config(),
            new FakeEnvironment("Testing"),
            lifetime,
            logger,
            ["anything"]);

        // The lifetime never signals ApplicationStarted, so completing at all proves the
        // short-circuit ran ahead of the wait: an in-memory TestServer opens no port to warm.
        await task.ExecuteAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task CancellationDuringTheServerWait_IsRethrown()
    {
        using var lifetime = new FakeLifetime();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var task = new ConfigurableWarmupTask(
            ServerWith("http://localhost:8080"),
            Config(),
            new FakeEnvironment("Production"),
            lifetime,
            new CapturingLogger(),
            ["anything"]);

        var act = () => task.ExecuteAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "shutdown is not a warm-up failure and must propagate to the runner");
    }

    [Fact]
    public async Task UnreachableEndpoint_IsLoggedAndNonFatal()
    {
        using var lifetime = new FakeLifetime();
        lifetime.SignalStarted();
        var logger = new CapturingLogger();
        var task = new ConfigurableWarmupTask(
            ServerWith($"http://localhost:{ClosedPort().ToString(CultureInfo.InvariantCulture)}"),
            Config(),
            new FakeEnvironment("Production"),
            lifetime,
            logger,
            ["anything"]);

        await task.ExecuteAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle()
            .Which.Level.Should().Be(
                LogLevel.Warning,
                "a warm-up that cannot reach its own endpoint falls back to lazy warm-up, it does not crash the host");
    }

    // ---------------------------------------------------------------- against a real h2c listener
    [Fact]
    public async Task ReplaysEveryPathInOrder_OverH2cPriorKnowledge()
    {
        await using var server = await TestServerHost.StartAsync(HttpProtocols.Http2, _ => StatusCodes.Status200OK);
        using var lifetime = new FakeLifetime();
        lifetime.SignalStarted();
        var logger = new CapturingLogger();

        var task = new ConfigurableWarmupTask(
            server.Server,
            Config(),
            new FakeEnvironment("Production"),
            lifetime,
            logger,
            ["Products/paged?pageNumber=1&pageSize=12", "Categories/lookup?nameProperty=Name"]);

        await task.ExecuteAsync(CancellationToken.None);

        server.RequestedPaths.Should().Equal(
            "/Products/paged?pageNumber=1&pageSize=12",
            "/Categories/lookup?nameProperty=Name");
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }

    /// <summary>
    /// The protected-endpoint profile: a 401 is the expected outcome of an unauthenticated
    /// self-request and still traverses everything the warm-up exists to JIT, so it must not end the
    /// run or log a failure.
    /// </summary>
    [Fact]
    public async Task NonSuccessStatus_IsIgnored_WhenSuccessIsNotRequired()
    {
        await using var server = await TestServerHost.StartAsync(HttpProtocols.Http2, _ => StatusCodes.Status401Unauthorized);
        using var lifetime = new FakeLifetime();
        lifetime.SignalStarted();
        var logger = new CapturingLogger();

        var task = new ConfigurableWarmupTask(
            server.Server,
            Config(),
            new FakeEnvironment("Production"),
            lifetime,
            logger,
            ["bookmarks?pageNumber=1&pageSize=10", "users?pageNumber=1&pageSize=10"],
            requireSuccessStatusCode: false);

        await task.ExecuteAsync(CancellationToken.None);

        server.RequestedPaths.Should().HaveCount(2, "every path still runs after a by-design refusal");
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }

    /// <summary>
    /// The output-cache profile: an anonymous read that answers non-2xx warmed nothing, so it is a
    /// real failure and the remaining paths are abandoned with it.
    /// </summary>
    [Fact]
    public async Task NonSuccessStatus_EndsTheRun_WhenSuccessIsRequired()
    {
        await using var server = await TestServerHost.StartAsync(HttpProtocols.Http2, _ => StatusCodes.Status500InternalServerError);
        using var lifetime = new FakeLifetime();
        lifetime.SignalStarted();
        var logger = new CapturingLogger();

        var task = new ConfigurableWarmupTask(
            server.Server,
            Config(),
            new FakeEnvironment("Production"),
            lifetime,
            logger,
            ["Events/paged?pageNumber=1", "Events?includeFKs=False"]);

        await task.ExecuteAsync(CancellationToken.None);

        server.RequestedPaths.Should().ContainSingle();
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    /// <summary>
    /// The Http1AndHttp2 profile (a service with no inbound gRPC server): overriding the version knob
    /// is what makes the same base talk to an HTTP/1.1-only listener.
    /// </summary>
    [Fact]
    public async Task Http11Override_WarmsAnHttp1OnlyListener()
    {
        await using var server = await TestServerHost.StartAsync(HttpProtocols.Http1, _ => StatusCodes.Status200OK);
        using var lifetime = new FakeLifetime();
        lifetime.SignalStarted();
        var logger = new CapturingLogger();

        var task = new ConfigurableWarmupTask(
            server.Server,
            Config(),
            new FakeEnvironment("Production"),
            lifetime,
            logger,
            ["Orders/paged?pageNumber=1&pageSize=10"],
            requestVersion: HttpVersion.Version11,
            requestVersionPolicy: HttpVersionPolicy.RequestVersionOrLower);

        await task.ExecuteAsync(CancellationToken.None);

        server.RequestedPaths.Should().ContainSingle();
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }

    /// <summary>
    /// The default h2c pin is not cosmetic: an Http2-only host needs it, and against an
    /// HTTP/1.1-only listener the exact pin fails rather than silently downgrading.
    /// </summary>
    [Fact]
    public async Task DefaultHttp2Pin_DoesNotDowngrade_AgainstAnHttp1OnlyListener()
    {
        await using var server = await TestServerHost.StartAsync(HttpProtocols.Http1, _ => StatusCodes.Status200OK);
        using var lifetime = new FakeLifetime();
        lifetime.SignalStarted();
        var logger = new CapturingLogger();

        var task = new ConfigurableWarmupTask(
            server.Server,
            Config(),
            new FakeEnvironment("Production"),
            lifetime,
            logger,
            ["Products/paged?pageNumber=1"]);

        await task.ExecuteAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    // ---------------------------------------------------------------- doubles
    private sealed class ConfigurableWarmupTask(
        IServer server,
        IConfiguration configuration,
        IHostEnvironment environment,
        IHostApplicationLifetime lifetime,
        ILogger logger,
        string[] paths,
        bool requireSuccessStatusCode = true,
        Version? requestVersion = null,
        HttpVersionPolicy requestVersionPolicy = HttpVersionPolicy.RequestVersionExact)
        : SelfHttpWarmupTaskBase(server, configuration, environment, lifetime, logger)
    {
        public override string Name => "TestWarmup";

        protected override IReadOnlyList<string> WarmupPaths => paths;

        protected override bool RequireSuccessStatusCode => requireSuccessStatusCode;

        protected override Version RequestVersion => requestVersion ?? HttpVersion.Version20;

        protected override HttpVersionPolicy RequestVersionPolicy => requestVersionPolicy;
    }

    private sealed class FakeServer(IFeatureCollection features) : IServer
    {
        public IFeatureCollection Features { get; } = features;

        public void Dispose()
        {
            // Nothing to release: the feature collection is inert.
        }

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "MMCA.Common.Aspire.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void SignalStarted() => _started.Cancel();

        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>
    /// A throwaway Kestrel listener on a dynamic loopback port that records what was requested. Real
    /// enough to prove the h2c prior-knowledge pin and the status-code semantics; it holds no
    /// application pipeline beyond a single terminal delegate.
    /// </summary>
    private sealed class TestServerHost(WebApplication app, List<string> requestedPaths) : IAsyncDisposable
    {
        public IServer Server => app.Services.GetRequiredService<IServer>();

        public IReadOnlyList<string> RequestedPaths => requestedPaths;

        public static async Task<TestServerHost> StartAsync(HttpProtocols protocols, Func<string, int> statusFor)
        {
            var requestedPaths = new List<string>();

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            // Loopback + dynamic port: Kestrel refuses dynamic binding on the "localhost" alias, and a
            // fixed port would make parallel test runs collide.
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Loopback, 0, endpoint => endpoint.Protocols = protocols));

            var app = builder.Build();
            app.Run(async context =>
            {
                lock (requestedPaths)
                {
                    requestedPaths.Add(context.Request.Path + context.Request.QueryString);
                }

                context.Response.StatusCode = statusFor(context.Request.Path);
                await context.Response.WriteAsync("warm");
            });

            await app.StartAsync();
            return new TestServerHost(app, requestedPaths);
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
