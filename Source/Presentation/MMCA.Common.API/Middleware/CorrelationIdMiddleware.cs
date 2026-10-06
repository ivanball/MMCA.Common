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
/// A blank header value is treated as absent, and a value longer than <see cref="MaxLength"/> is
/// cut to that length (the width of every persisted correlation column), so the id the client sees
/// echoed is exactly the one stored. A value with a non-ASCII or control character is replaced by
/// the generated id, because Kestrel refuses to echo it in a response header and the request would
/// fail after its handler ran.
/// </summary>
/// <param name="next">The next middleware in the pipeline.</param>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    /// <summary>The HTTP header name used for the correlation ID.</summary>
    public const string HeaderName = "X-Correlation-ID";

    /// <summary>The longest correlation id kept: the width of the persisted correlation columns.</summary>
    internal const int MaxLength = 64;

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

        var header = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = (string.IsNullOrWhiteSpace(header) ? null : Sanitize(header))
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

    /// <summary>
    /// Cuts a caller-supplied id to <see cref="MaxLength"/> characters and keeps it only when every
    /// character is printable ASCII.
    /// </summary>
    /// <param name="value">The header value.</param>
    /// <returns>The value, at most <see cref="MaxLength"/> characters long, or null when it cannot be echoed.</returns>
    private static string? Sanitize(string value)
    {
        var cut = value.Length > MaxLength ? value[..MaxLength] : value;
        return cut.All(static c => c is >= ' ' and <= '~') ? cut : null;
    }
}
