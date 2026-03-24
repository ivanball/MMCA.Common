using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that logs query execution with correlation ID and duration.
/// Adds a structured logging scope for consistent trace context.
/// </summary>
/// <typeparam name="TQuery">The query type.</typeparam>
/// <typeparam name="TResult">The result type returned by the handler.</typeparam>
public sealed partial class LoggingQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    ICorrelationContext correlationContext,
    ILogger<LoggingQueryDecorator<TQuery, TResult>> logger) : IQueryHandler<TQuery, TResult>
{
    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default)
    {
        var queryName = typeof(TQuery).Name;
        var correlationId = correlationContext.CorrelationId;

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["QueryName"] = queryName,
        }))
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                LogQueryCompleted(logger, queryName, stopwatch.ElapsedMilliseconds, correlationId);
                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                LogQueryException(logger, queryName, stopwatch.ElapsedMilliseconds, correlationId, ex);
                throw;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Query {QueryName} completed in {ElapsedMs}ms [CorrelationId: {CorrelationId}]")]
    private static partial void LogQueryCompleted(ILogger logger, string queryName, long elapsedMs, string correlationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Query {QueryName} threw after {ElapsedMs}ms [CorrelationId: {CorrelationId}]")]
    private static partial void LogQueryException(ILogger logger, string queryName, long elapsedMs, string correlationId, Exception exception);
}
