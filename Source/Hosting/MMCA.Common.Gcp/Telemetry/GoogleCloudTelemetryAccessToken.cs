using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace MMCA.Common.Gcp.Telemetry;

/// <summary>
/// Holds the current access token the exporter's <see cref="GoogleCloudTelemetryAuthHandler"/> reads
/// on every request, and refreshes it in the background from the
/// <see cref="IGoogleCloudTelemetryTokenSource"/>.
/// <para>
/// Why a held token rather than asking the source inside the handler: the OTLP/HTTP exporter sends
/// on its batch thread through the SYNCHRONOUS <c>HttpClient.Send</c>, while every Google credential
/// hands out tokens asynchronously only. Reading a token refreshed here keeps the send path free of
/// any blocking wait on a task.
/// </para>
/// <para>
/// The refresh interval is one minute for the reason the Cloud SQL sibling gives: Google's credential
/// returns its cached token until fewer than 225 seconds remain (refreshing in the background) and
/// fetches a new one only under 60, so a token read once a minute is always still valid when used.
/// The first token is fetched while the host starts, so the first export already carries one. A host
/// without credentials still starts: the failure is logged, the refresh retries every 30 seconds, and
/// until it succeeds the handler refuses each export locally instead of sending it unauthenticated.
/// </para>
/// </summary>
/// <param name="tokenSource">Where the tokens come from.</param>
/// <param name="logger">Logs a failed refresh.</param>
internal sealed partial class GoogleCloudTelemetryAccessToken(
    IGoogleCloudTelemetryTokenSource tokenSource,
    ILogger<GoogleCloudTelemetryAccessToken> logger) : IHostedService, IDisposable
{
    /// <summary>How often a held token is re-read from the source.</summary>
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    /// <summary>How soon a failed refresh is retried.</summary>
    internal static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _stopping = new();
    private volatile string? _current;
    private Task? _refreshLoop;

    /// <summary>The current access token, or <see langword="null"/> before the first successful refresh.</summary>
    public string? Current => _current;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var refreshed = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        _refreshLoop = RefreshLoopAsync(refreshed, _stopping.Token);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_refreshLoop is not null)
        {
            await _refreshLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    /// <summary>
    /// Reads a token from the source and holds it. A failure keeps the last good token (it may still
    /// be valid) and is logged, never thrown: telemetry export is not worth failing the host over.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="true"/> when a token was read.</returns>
    internal async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        // The credential's own HTTP calls (the metadata server, the OAuth2 token endpoint) would
        // otherwise be traced by the HttpClient instrumentation and exported once a minute as noise.
        using var suppressInstrumentation = SuppressInstrumentationScope.Begin();
        try
        {
            _current = await tokenSource.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogRefreshFailed(logger, ex, FailureRetryInterval.TotalSeconds);
            return false;
        }
    }

    private async Task RefreshLoopAsync(bool lastSucceeded, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(lastSucceeded ? RefreshInterval : FailureRetryInterval, stoppingToken).ConfigureAwait(false);
                lastSucceeded = await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the Google Cloud telemetry access token failed; traces and logs are not exported until a refresh succeeds (retrying in {RetrySeconds} seconds)")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception, double retrySeconds);
}
