using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace MMCA.Common.Gateway.Forwarding;

/// <summary>
/// YARP's forwarder client factory with the opt-in connect-failure retry installed as the OUTERMOST
/// handler of every cluster client, so a retried attempt goes through the whole inner pipeline again.
/// Registered by <c>AddMmcaGateway</c> only when <c>MmcaGateway:Retry:Enabled</c> is true.
/// </summary>
internal sealed class GatewayRetryForwarderHttpClientFactory : ForwarderHttpClientFactory
{
    private readonly ILogger _retryLogger;

    private readonly GatewayRetrySettings _settings;

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// The constructor the container uses: YARP's own factory logging keeps its category, and the
    /// retry Warning is logged under the retry handler's category.
    /// </summary>
    /// <param name="loggerFactory">Creates both loggers.</param>
    /// <param name="settings">The gateway settings, whose <see cref="GatewaySettings.Retry"/> block is used.</param>
    /// <param name="timeProvider">The clock the delay between attempts is measured on.</param>
    public GatewayRetryForwarderHttpClientFactory(
        ILoggerFactory loggerFactory,
        IOptions<GatewaySettings> settings,
        TimeProvider timeProvider)
        : base(CreateYarpLogger(loggerFactory))
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _retryLogger = loggerFactory.CreateLogger<GatewayConnectRetryHandler>();
        _settings = settings.Value.Retry;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Builds the factory around one logger for the retry Warning; YARP's own factory logging is off.
    /// </summary>
    /// <param name="retryLogger">Logs the retry Warning.</param>
    /// <param name="settings">The gateway settings, whose <see cref="GatewaySettings.Retry"/> block is used.</param>
    /// <param name="timeProvider">The clock the delay between attempts is measured on.</param>
    public GatewayRetryForwarderHttpClientFactory(
        ILogger retryLogger,
        IOptions<GatewaySettings> settings,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(retryLogger);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _retryLogger = retryLogger;
        _settings = settings.Value.Retry;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    protected override HttpMessageHandler WrapHandler(ForwarderHttpClientContext context, HttpMessageHandler handler) =>
        new GatewayConnectRetryHandler(_settings, _timeProvider, _retryLogger)
        {
            InnerHandler = base.WrapHandler(context, handler),
        };

    private static ILogger<ForwarderHttpClientFactory> CreateYarpLogger(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        return loggerFactory.CreateLogger<ForwarderHttpClientFactory>();
    }
}
