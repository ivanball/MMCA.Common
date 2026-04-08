using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Grpc.Diagnostics;

/// <summary>
/// Diagnostic <see cref="DelegatingHandler"/> that logs the resolved request URI and HTTP
/// version of every outgoing gRPC call. Used to verify Aspire service discovery is picking
/// the expected scheme/port and that the request is actually HTTP/2 before reaching the wire.
/// </summary>
public sealed partial class GrpcRequestLoggingHandler(ILogger<GrpcRequestLoggingHandler> logger) : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (logger.IsEnabled(LogLevel.Information))
        {
            LogOutgoingRequest(logger, request.RequestUri, request.Version, request.VersionPolicy);
        }

        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (logger.IsEnabled(LogLevel.Information))
            {
                LogResponseReceived(logger, request.RequestUri, response.StatusCode, response.Version);
            }

            return response;
        }
        catch (Exception ex)
        {
            LogRequestFailed(logger, ex, request.RequestUri, request.Version);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "gRPC outgoing: {Uri} (HTTP version {Version}, policy {VersionPolicy})")]
    private static partial void LogOutgoingRequest(ILogger logger, Uri? uri, Version version, HttpVersionPolicy versionPolicy);

    [LoggerMessage(Level = LogLevel.Information, Message = "gRPC response: {Uri} -> {StatusCode} (HTTP version {Version})")]
    private static partial void LogResponseReceived(ILogger logger, Uri? uri, System.Net.HttpStatusCode statusCode, Version version);

    [LoggerMessage(Level = LogLevel.Error, Message = "gRPC request failed: {Uri} (HTTP version {Version})")]
    private static partial void LogRequestFailed(ILogger logger, Exception ex, Uri? uri, Version version);
}
