using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace MMCA.Common.Aws.Telemetry;

/// <summary>
/// Resolves the AWS default credential chain once while the host starts, so the first export already
/// finds a credential (the twin of the Google exporter's start-up token fetch). Registered only when
/// <c>AwsTelemetry:Region</c> is set and no static key pair is configured.
/// <para>
/// It never fails the host: telemetry export is not worth refusing to start over. A failure, or a
/// chain that finds nothing, is logged once as a Warning naming the exception type and message (never
/// key material), and each export then tries the chain again until one succeeds.
/// </para>
/// </summary>
/// <param name="credentials">The default-chain credential source the exporters share.</param>
/// <param name="logger">Logs a failed warm-up.</param>
internal sealed partial class AwsTelemetryCredentialWarmup(
    IAwsTelemetryCredentialSource credentials,
    ILogger<AwsTelemetryCredentialWarmup> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The chain's own HTTP calls (the container or instance metadata endpoint, STS) would otherwise
        // be traced by the HttpClient instrumentation and exported as noise.
        using var suppressInstrumentation = SuppressInstrumentationScope.Begin();
        try
        {
            var resolved = await credentials.GetAsync(cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                LogNoCredential(logger);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is stopping before it started; the next export resolves the chain itself.
        }
#pragma warning disable CA1031 // A warm-up is a latency optimization: the next export resolves the credential chain itself
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogWarmupFailed(logger, exception.GetType().FullName, exception.Message, exception);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Resolving the AWS credential for telemetry export at start-up failed ({ExceptionType}: {ExceptionMessage}); each export tries the default chain again until one succeeds, and sends nothing until then.")]
    private static partial void LogWarmupFailed(ILogger logger, string? exceptionType, string exceptionMessage, Exception exception);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "No AWS credential was found for telemetry export at start-up (set AwsTelemetry:AccessKeyId and AwsTelemetry:SecretAccessKey, or give the host an IAM role); each export tries the default chain again until one succeeds, and sends nothing until then.")]
    private static partial void LogNoCredential(ILogger logger);
}
