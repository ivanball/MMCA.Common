using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// Middleware that extracts or generates a correlation ID for each HTTP request.
/// The ID is read from the <c>X-Correlation-ID</c> request header if present;
/// otherwise falls back to the current W3C trace ID (from OpenTelemetry/Activity)
/// or the ASP.NET Core <see cref="HttpContext.TraceIdentifier"/>.
/// The correlation ID is echoed back in the response header for client-side tracing.
/// </summary>
/// <param name="next">The next middleware in the pipeline.</param>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    /// <summary>The HTTP header name used for the correlation ID.</summary>
    public const string HeaderName = "X-Correlation-ID";

    /// <summary>
    /// Processes the HTTP request by setting the correlation ID on the scoped
    /// <see cref="ICorrelationContext"/> and echoing it in the response header.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <param name="correlationContext">The scoped correlation context to populate.</param>
    /// <returns>A task representing the middleware execution.</returns>
    public async Task InvokeAsync(HttpContext context, ICorrelationContext correlationContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(correlationContext);

        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault()
            ?? Activity.Current?.TraceId.ToString()
            ?? context.TraceIdentifier;

        correlationContext.SetCorrelationId(correlationId);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        await next(context).ConfigureAwait(false);
    }
}
