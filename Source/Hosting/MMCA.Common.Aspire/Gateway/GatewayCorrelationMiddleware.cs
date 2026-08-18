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
/// It writes the value onto the REQUEST headers when the caller did not supply one, so the proxied
/// request carries it downstream and the service-side <c>CorrelationIdMiddleware</c> adopts the
/// same ID instead of minting a second one. The response echo runs from
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
    /// Ensures the request carries a correlation ID, echoes it on the response, then invokes the
    /// rest of the pipeline.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <returns>A task representing the middleware execution.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            // Prefer the W3C trace id so the correlation ID and the distributed trace line up in
            // the backend; TraceIdentifier is the fallback when no Activity is running (tracing
            // disabled, or a request that arrived before the instrumentation started one).
            correlationId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
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
