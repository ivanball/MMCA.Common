using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that logs query execution with correlation ID, duration, and success/failure
/// status. Adds a structured logging scope for consistent trace context.
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
            var outcome = "completed";
            try
            {
                var result = await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                if (result is Shared.Abstractions.Result { IsFailure: true } failureResult)
                {
                    outcome = "failed";
                    var errorSummary = string.Join("; ", failureResult.Errors.Select(e => $"{e.Code}: {e.Message}"));
                    LogQueryFailed(logger, queryName, stopwatch.ElapsedMilliseconds, correlationId, errorSummary);
                }
                else
                {
                    LogQueryCompleted(logger, queryName, stopwatch.ElapsedMilliseconds, correlationId);
                }

                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                outcome = "exception";
                LogQueryException(logger, queryName, stopwatch.ElapsedMilliseconds, correlationId, ex);
                throw;
            }
            finally
            {
                CqrsMetrics.QueryDuration.Record(
                    stopwatch.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("query", queryName),
                    new KeyValuePair<string, object?>("outcome", outcome));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Query {QueryName} completed in {ElapsedMs}ms [CorrelationId: {CorrelationId}]")]
    private static partial void LogQueryCompleted(ILogger logger, string queryName, long elapsedMs, string correlationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query {QueryName} failed after {ElapsedMs}ms [CorrelationId: {CorrelationId}]: {ErrorSummary}")]
    private static partial void LogQueryFailed(ILogger logger, string queryName, long elapsedMs, string correlationId, string errorSummary);

    [LoggerMessage(Level = LogLevel.Error, Message = "Query {QueryName} threw after {ElapsedMs}ms [CorrelationId: {CorrelationId}]")]
    private static partial void LogQueryException(ILogger logger, string queryName, long elapsedMs, string correlationId, Exception exception);
}
