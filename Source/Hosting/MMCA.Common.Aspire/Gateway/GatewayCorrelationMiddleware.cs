using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MMCA.Common.Aspire.Gateway;

/// <summary>
/// Stamps a correlation ID on every request entering the edge and echoes it back on the response.
/// <para>
/// This is the gateway twin of <c>MMCA.Common.API.Middleware.CorrelationIdMiddleware</c> and exists
/// precisely because that one cannot run here: it writes the ID onto a scoped
/// <c>ICorrelationContext</c>, which only a host that has registered the Common application
/// services owns. A reverse-proxy gateway forwards bytes; it has no application container and no
/// correlation context to populate. This middleware therefore takes NO dependency beyond the
/// <see cref="RequestDelegate"/>, which is what makes it safe to drop into a bare YARP host.
/// </para>
/// <para>
/// It writes the value onto the REQUEST headers when the caller did not supply a usable one, so the
/// proxied request carries it downstream and the service-side <c>CorrelationIdMiddleware</c> adopts
/// the same ID instead of minting a second one. A caller-supplied value follows the service's rule
/// exactly: cut to <see cref="MaxLength"/> (the stored width), and replaced by a generated id when
/// it has a non-ASCII or control character (Kestrel refuses to echo one). The response echo runs from
/// <see cref="HttpResponse.OnStarting(Func{Task})"/>, so it survives a proxied response whose
/// headers are written by the forwarder.
/// </para>
/// </summary>
/// <param name="next">The next middleware in the pipeline.</param>
public sealed class GatewayCorrelationMiddleware(RequestDelegate next)
{
    /// <summary>
    /// The HTTP header name used for the correlation ID. Deliberately the same literal as
    /// <c>CorrelationIdMiddleware.HeaderName</c> in MMCA.Common.API: the two packages share no
    /// reference, and the whole point of the pair is that the edge and the services agree on it.
    /// </summary>
    public const string HeaderName = "X-Correlation-ID";

    /// <summary>
    /// The longest correlation id kept. Twin of <c>CorrelationIdMiddleware.MaxLength</c> in
    /// MMCA.Common.API (the persisted column width); the two packages share no reference, so the
    /// literal is repeated on purpose.
    /// </summary>
    internal const int MaxLength = 64;

    /// <summary>
    /// Ensures the request carries a correlation ID, echoes it on the response, then invokes the
    /// rest of the pipeline.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <returns>A task representing the middleware execution.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var header = context.Request.Headers[HeaderName].FirstOrDefault();

        // The service's rule (CorrelationIdMiddleware.Sanitize), mirrored because the packages share
        // no reference: this echo runs from OnStarting after the forwarder copied the service's
        // headers, so the edge must forward and echo exactly what the service keeps and stores.
        // Prefer the W3C trace id for a generated one so the correlation ID and the distributed
        // trace line up; TraceIdentifier is the fallback when no Activity is running.
        var correlationId = (string.IsNullOrWhiteSpace(header) ? null : Sanitize(header))
            ?? Activity.Current?.TraceId.ToString()
            ?? context.TraceIdentifier;

        if (!string.Equals(correlationId, header, StringComparison.Ordinal))
        {
            context.Request.Headers[HeaderName] = correlationId;
        }

        var echoed = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = echoed;
            return Task.CompletedTask;
        });

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Cuts a caller-supplied id to <see cref="MaxLength"/> characters and keeps it only when every
    /// character is printable ASCII (the service's rule).
    /// </summary>
    /// <param name="value">The header value.</param>
    /// <returns>The value, at most <see cref="MaxLength"/> characters long, or null when it cannot be echoed.</returns>
    private static string? Sanitize(string value)
    {
        var cut = value.Length > MaxLength ? value[..MaxLength] : value;
        return cut.All(static c => c is >= ' ' and <= '~') ? cut : null;
    }
}

/// <summary>Pipeline extension for <see cref="GatewayCorrelationMiddleware"/>.</summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class GatewayCorrelationExtensions
{
    extension(IApplicationBuilder app)
    {
        /// <summary>
        /// Adds <see cref="GatewayCorrelationMiddleware"/> to the request pipeline. Call it FIRST,
        /// before the proxy/forwarder is mapped, so the ID is on the request the gateway forwards.
        /// </summary>
        /// <returns>The same application builder for chaining.</returns>
        public IApplicationBuilder UseGatewayCorrelation()
        {
            ArgumentNullException.ThrowIfNull(app);
            return app.UseMiddleware<GatewayCorrelationMiddleware>();
        }
    }
}
