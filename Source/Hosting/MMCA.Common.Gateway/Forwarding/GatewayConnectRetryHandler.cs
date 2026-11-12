using Microsoft.Extensions.Logging;

namespace MMCA.Common.Gateway.Forwarding;

/// <summary>
/// The opt-in connect-failure retry on the client YARP builds for each cluster. Only a request that
/// never reached the destination is retried: a body-less GET or HEAD whose attempt failed with a name
/// resolution, connection or TLS failure. A response of any status, any other exception and a
/// cancellation pass through untouched, so nothing the destination may have acted on is ever sent twice.
/// <para>
/// At most <see cref="GatewayRetrySettings.MaxRetryAttempts"/> extra attempts are made, each after
/// <see cref="GatewayRetrySettings.Delay"/> on the injected clock. When the budget is spent the last
/// exception is rethrown unchanged, so YARP still answers its usual 502 and the passive health policy
/// counts one failure.
/// </para>
/// </summary>
/// <param name="settings">The retry settings.</param>
/// <param name="timeProvider">The clock the delay between attempts is measured on.</param>
/// <param name="logger">Logs one Warning per retry.</param>
internal sealed partial class GatewayConnectRetryHandler(
    GatewayRetrySettings settings,
    TimeProvider timeProvider,
    ILogger logger) : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return IsRetryable(request)
            ? SendWithRetryAsync(request, cancellationToken)
            : base.SendAsync(request, cancellationToken);
    }

    /// <summary>A body-less GET or HEAD: the only requests a replay cannot change the meaning of.</summary>
    private static bool IsRetryable(HttpRequestMessage request) =>
        request.Content is null
        && (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head);

    /// <summary>The attempt never reached the destination, so it cannot have acted on the request.</summary>
    private static bool IsConnectFailure(HttpRequestException exception) =>
        exception.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError;

    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var retries = 0;
        while (true)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (
                IsConnectFailure(exception)
                && retries < settings.MaxRetryAttempts
                && !cancellationToken.IsCancellationRequested)
            {
                retries++;
                LogRetrying(
                    logger,
                    request.Method.Method,
                    request.RequestUri?.Authority ?? "(no destination)",
                    exception.HttpRequestError,
                    retries,
                    settings.MaxRetryAttempts);
            }

            if (settings.Delay > TimeSpan.Zero)
            {
                await Task.Delay(settings.Delay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Retrying {Method} to {Authority} after a connect failure ({HttpRequestError}), retry {Retry} of {MaxRetryAttempts}")]
    private static partial void LogRetrying(
        ILogger logger,
        string method,
        string authority,
        HttpRequestError httpRequestError,
        int retry,
        int maxRetryAttempts);
}
