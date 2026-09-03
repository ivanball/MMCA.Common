using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Conventions;

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

        using (BeginQueryScope(logger, queryName, ModuleName, correlationId))
        {
            // Timestamp rather than a Stopwatch instance: same resolution, one fewer allocation per
            // query. Elapsed is captured before logging, so the recorded duration stays the handler's
            // (matching the previous stopwatch.Stop()-then-log ordering).
            var startTimestamp = Stopwatch.GetTimestamp();
            try
            {
                var result = await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp);

                if (result is Shared.Abstractions.Result { IsFailure: true } failureResult)
                {
                    var errorSummary = string.Join("; ", failureResult.Errors.Select(e => $"{e.Code}: {e.Message}"));
                    LogQueryFailed(logger, queryName, (long)elapsed.TotalMilliseconds, correlationId, errorSummary);
                    RecordDuration(queryName, elapsed, "failed");
                }
                else
                {
                    LogQueryCompleted(logger, queryName, (long)elapsed.TotalMilliseconds, correlationId);
                    RecordDuration(queryName, elapsed, "completed");
                }

                return result;
            }
            catch (Exception ex)
            {
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                LogQueryException(logger, queryName, (long)elapsed.TotalMilliseconds, correlationId, ex);
                RecordDuration(queryName, elapsed, "exception");
                throw;
            }
        }
    }

    /// <summary>
    /// Source-generated scope, matching <c>LoggingCommandDecorator</c>: the previous
    /// <c>BeginScope(new Dictionary&lt;string, object&gt;)</c> allocated a dictionary and boxed the
    /// scope state on every query, at every log level, including when logging was disabled entirely.
    /// </summary>
    private static readonly Func<ILogger, string, string, string, IDisposable?> BeginQueryScope =
        LoggerMessage.DefineScope<string, string, string>(
            "Query {QueryName} [Module: {ModuleName}] [CorrelationId: {CorrelationId}]");

    /// <summary>
    /// Owning module of the query, derived once per closed generic type (the static field is per
    /// <c>TQuery</c>), so every log line a handler emits can be filtered by module without paying
    /// for the namespace parse per execution.
    /// </summary>
    private static readonly string ModuleName = ModuleNameConventions.GetModuleName(typeof(TQuery)) ?? "unknown";

    private static void RecordDuration(string queryName, TimeSpan elapsed, string outcome) =>
        CqrsMetrics.QueryDuration.Record(
            elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("query", queryName),
            new KeyValuePair<string, object?>("outcome", outcome));

    [LoggerMessage(Level = LogLevel.Debug, Message = "Query {QueryName} completed in {ElapsedMs}ms [CorrelationId: {CorrelationId}]")]
    private static partial void LogQueryCompleted(ILogger logger, string queryName, long elapsedMs, string correlationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query {QueryName} failed after {ElapsedMs}ms [CorrelationId: {CorrelationId}]: {ErrorSummary}")]
    private static partial void LogQueryFailed(ILogger logger, string queryName, long elapsedMs, string correlationId, string errorSummary);

    [LoggerMessage(Level = LogLevel.Error, Message = "Query {QueryName} threw after {ElapsedMs}ms [CorrelationId: {CorrelationId}]")]
    private static partial void LogQueryException(ILogger logger, string queryName, long elapsedMs, string correlationId, Exception exception);
}
