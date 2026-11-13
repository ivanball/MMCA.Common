using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Aspire.Warmup;

/// <summary>
/// Base class for the ADR-025 self-HTTP warm-up: once the server has started, replay a short list of
/// hot read paths against this host's own Kestrel endpoint. That warms the FULL request path
/// (ingress connection, Kestrel, output cache, routing, authentication, controller, EF Core, SQL),
/// which is where the cold-start cost of an idle, CPU-throttled replica lives.
/// <para>
/// Register a derived task with <c>AddWarmupTask&lt;T&gt;()</c>; the <c>AddWarmupReadiness()</c>
/// runner from <c>AddServiceDefaults()</c> executes it, so <c>/health/ready</c> stays not-ready until
/// the warm-up has had its chance. Failures are logged and never fatal: the host falls back to lazy
/// warm-up on the first real request rather than keeping the replica out of rotation.
/// </para>
/// </summary>
/// <param name="server">The running server, used to discover the actual bound cleartext address.</param>
/// <param name="configuration">Configuration consulted for the <c>ASPNETCORE_URLS</c> fallback.</param>
/// <param name="environment">Host environment; the warm-up is skipped under the Testing environment.</param>
/// <param name="lifetime">Host lifetime, awaited so the requests start only once Kestrel is listening.</param>
/// <param name="logger">Logger for warm-up diagnostics.</param>
public abstract partial class SelfHttpWarmupTaskBase(
    IServer server,
    IConfiguration configuration,
    IHostEnvironment environment,
    IHostApplicationLifetime lifetime,
    ILogger logger) : IWarmupTask
{
    /// <summary>
    /// Port used when neither the server nor <c>ASPNETCORE_URLS</c> yields a usable cleartext
    /// address: the containerized default every deployed service listens on.
    /// </summary>
    public const int DefaultPort = 8080;

    /// <summary>
    /// The environment name whose hosts have no real Kestrel port. Integration tests boot a service
    /// through <c>WebApplicationFactory</c>, whose in-memory TestServer never opens a socket, so a
    /// self-HTTP request could only ever fail there.
    /// </summary>
    private const string TestingEnvironmentName = "Testing";

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <summary>
    /// The paths to replay, relative to the host's own base address, in the order they are issued.
    /// <para>
    /// These must match the URLs real callers issue character for character in their VALUES, not
    /// just their shape: an output-cache policy that varies by query string keys the entry on the
    /// exact URL, so a warmed entry built from different values is an entry nothing ever reads.
    /// </para>
    /// </summary>
    protected abstract IReadOnlyList<string> WarmupPaths { get; }

    /// <summary>
    /// HTTP version the warm-up requests are sent with. Defaults to HTTP/2, which is required on a
    /// host whose cleartext endpoints are Http2-only (h2c prior knowledge): paired with
    /// <see cref="RequestVersionPolicy"/> it prevents the silent downgrade to HTTP/1.1 that such an
    /// endpoint rejects with 400 "An HTTP/1.x request was sent to an HTTP/2 only endpoint", which
    /// would fail the warm-up on every single startup. A host that stays Http1AndHttp2 (no inbound
    /// gRPC server) overrides both members with <see cref="HttpVersion.Version11"/> and
    /// <see cref="HttpVersionPolicy.RequestVersionOrLower"/>.
    /// </summary>
    protected virtual Version RequestVersion => HttpVersion.Version20;

    /// <summary>
    /// Version-negotiation policy for the warm-up requests. Defaults to
    /// <see cref="HttpVersionPolicy.RequestVersionExact"/> so <see cref="RequestVersion"/> is a pin,
    /// not a preference.
    /// </summary>
    protected virtual HttpVersionPolicy RequestVersionPolicy => HttpVersionPolicy.RequestVersionExact;

    /// <summary>
    /// Whether a non-success status ends the warm-up. Defaults to <see langword="true"/>, which suits
    /// an anonymous read whose response body is what populates the output cache.
    /// <para>
    /// Override with <see langword="false"/> for a protected endpoint: an unauthenticated
    /// self-request against an <c>[Authorize]</c> route gets 401 BY DESIGN, and that refusal still
    /// traverses Kestrel, routing, authentication and the middleware pipeline, which is the JIT cost
    /// the warm-up exists to pay down. Treating it as a failure logs a spurious warning on every
    /// startup and skips the remaining paths.
    /// </para>
    /// </summary>
    protected virtual bool RequireSuccessStatusCode => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (environment.IsEnvironment(TestingEnvironmentName))
        {
            return;
        }

        try
        {
            await WaitForServerStartedAsync(cancellationToken).ConfigureAwait(false);

            var baseAddress = new UriBuilder
            {
                Scheme = Uri.UriSchemeHttp,
                Host = "localhost",
                Port = ResolveWarmupPort(server, configuration),
            }.Uri;

            using var handler = new SocketsHttpHandler();
            using var httpClient = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = baseAddress,
                DefaultRequestVersion = RequestVersion,
                DefaultVersionPolicy = RequestVersionPolicy,
            };

            foreach (var path in WarmupPaths)
            {
                using var response = await httpClient
                    .GetAsync(new Uri(path, UriKind.Relative), cancellationToken)
                    .ConfigureAwait(false);

                if (RequireSuccessStatusCode)
                {
                    response.EnsureSuccessStatusCode();

                    // Draining the body is the point for an output-cache warm-up: the entry is only
                    // worth priming if the whole response was produced and transferred.
                    _ = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            LogWarmupCompleted(logger, Name, WarmupPaths.Count, baseAddress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // warm-up failures are non-fatal by design: log and fall back to lazy warm-up
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogWarmupFailed(logger, ex, Name);
        }
    }

    /// <summary>
    /// Resolves the port to self-request. Prefers the server's actual bound cleartext address, which
    /// is the only correct answer under Aspire's dynamic ports, and falls back to the
    /// <c>ASPNETCORE_URLS</c> value and finally to <see cref="DefaultPort"/>.
    /// </summary>
    /// <param name="server">The running server.</param>
    /// <param name="configuration">Configuration carrying the <c>ASPNETCORE_URLS</c> fallback.</param>
    /// <returns>The resolved cleartext port.</returns>
    internal static int ResolveWarmupPort(IServer server, IConfiguration configuration)
    {
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses
            .FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? SelectCleartextUrl(configuration["ASPNETCORE_URLS"]);

        return int.TryParse(address?.TrimEnd('/').Split(':')[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            ? port
            : DefaultPort;
    }

    /// <summary>
    /// Picks the cleartext entry out of an <c>ASPNETCORE_URLS</c> value. The variable may hold a
    /// semicolon-separated list (for example <c>https://+:443;http://+:8080/</c>), and entries
    /// commonly carry a trailing slash, which the caller trims before reading the port. Wildcard
    /// hosts such as <c>+</c> and <c>*</c> are why this stays string handling: Uri rejects them.
    /// </summary>
    /// <param name="configuredUrls">The raw configuration value, possibly null.</param>
    /// <returns>The chosen url, or null when nothing was configured.</returns>
    private static string? SelectCleartextUrl(string? configuredUrls)
    {
        if (string.IsNullOrWhiteSpace(configuredUrls))
        {
            return null;
        }

        var entries = configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return Array.Find(entries, e => e.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? (entries.Length > 0 ? entries[0] : null);
    }

    // The warm-up runner is a hosted service that starts BEFORE Kestrel begins listening (the web
    // host is the last hosted service), so wait for ApplicationStarted before self-requesting.
    private async Task WaitForServerStartedAsync(CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = lifetime.ApplicationStarted
            .Register(() => started.TrySetResult())
            .ConfigureAwait(false);

        await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "HTTP warm-up {TaskName} completed: {PathCount} path(s) replayed against {BaseAddress}.")]
    private static partial void LogWarmupCompleted(ILogger logger, string taskName, int pathCount, Uri baseAddress);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "HTTP warm-up {TaskName} failed: first requests may be slow.")]
    private static partial void LogWarmupFailed(ILogger logger, Exception exception, string taskName);
}
