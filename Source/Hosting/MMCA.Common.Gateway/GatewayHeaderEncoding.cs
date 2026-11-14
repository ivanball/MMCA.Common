using System.Text;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace MMCA.Common.Gateway;

/// <summary>
/// Lets the gateway's Kestrel read the correlation header as Latin-1 instead of refusing it. Kestrel
/// reads request headers as ASCII and answers a non-ASCII byte with an empty 400 and
/// <c>Connection: close</c>, before any middleware runs, so <c>GatewayCorrelationMiddleware</c> (in
/// MMCA.Common.Aspire) never got to replace the unusable value with a generated id as it promises
/// (Run 17 X-42). Read as Latin-1, every byte decodes, the middleware sees a non-ASCII character and
/// forwards and echoes a generated id instead. Every other header keeps Kestrel's rule.
/// </summary>
internal static class GatewayHeaderEncoding
{
    /// <summary>
    /// The correlation header. Twin of <c>GatewayCorrelationMiddleware.HeaderName</c> in
    /// MMCA.Common.Aspire: the two packages share no reference, so the literal is repeated on purpose.
    /// </summary>
    internal const string CorrelationHeaderName = "X-Correlation-ID";

    /// <summary>
    /// Chains the Latin-1 rule for <see cref="CorrelationHeaderName"/> in front of any selector the host
    /// set itself, which keeps deciding every other header.
    /// </summary>
    /// <param name="options">The Kestrel options to adjust.</param>
    internal static void Apply(KestrelServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var hostSelector = options.RequestHeaderEncodingSelector;
        options.RequestHeaderEncodingSelector = headerName =>
            string.Equals(headerName, CorrelationHeaderName, StringComparison.OrdinalIgnoreCase)
                ? Encoding.Latin1
                : hostSelector?.Invoke(headerName);
    }
}
