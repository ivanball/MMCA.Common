using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Outermost decorator that logs command execution with correlation ID, duration,
/// and success/failure status. Adds a structured logging scope so that all log
/// entries emitted by inner decorators and the handler share the same correlation context.
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">The result type returned by the handler.</typeparam>
public sealed partial class LoggingCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ICorrelationContext correlationContext,
    ILogger<LoggingCommandDecorator<TCommand, TResult>> logger) : ICommandHandler<TCommand, TResult>
{
    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        var commandName = typeof(TCommand).Name;
        var correlationId = correlationContext.CorrelationId;

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["CommandName"] = commandName,
        }))
        {
            LogCommandStarted(logger, commandName, correlationId);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                if (result is Shared.Abstractions.Result { IsFailure: true } failureResult)
                {
                    var errorSummary = string.Join("; ", failureResult.Errors.Select(e => $"{e.Code}: {e.Message}"));
                    LogCommandFailed(logger, commandName, stopwatch.ElapsedMilliseconds, correlationId, errorSummary);
                }
                else
                {
                    LogCommandCompleted(logger, commandName, stopwatch.ElapsedMilliseconds, correlationId);
                }

                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                LogCommandException(logger, commandName, stopwatch.ElapsedMilliseconds, correlationId, ex);
                throw;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Executing command {CommandName} [CorrelationId: {CorrelationId}]")]
    private static partial void LogCommandStarted(ILogger logger, string commandName, string correlationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {CommandName} completed in {ElapsedMs}ms [CorrelationId: {CorrelationId}]")]
    private static partial void LogCommandCompleted(ILogger logger, string commandName, long elapsedMs, string correlationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command {CommandName} failed after {ElapsedMs}ms [CorrelationId: {CorrelationId}] — {ErrorSummary}")]
    private static partial void LogCommandFailed(ILogger logger, string commandName, long elapsedMs, string correlationId, string errorSummary);

    [LoggerMessage(Level = LogLevel.Error, Message = "Command {CommandName} threw after {ElapsedMs}ms [CorrelationId: {CorrelationId}]")]
    private static partial void LogCommandException(ILogger logger, string commandName, long elapsedMs, string correlationId, Exception exception);
}
