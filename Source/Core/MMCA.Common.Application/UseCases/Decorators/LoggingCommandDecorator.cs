using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Conventions;

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

        using (BeginCommandScope(logger, commandName, ModuleName, correlationId))
        {
            LogCommandStarted(logger, commandName, correlationId);

            // Timestamp rather than a Stopwatch instance: same resolution, one fewer allocation per
            // command. Elapsed is captured before logging, so the recorded duration stays the
            // handler's (matching the previous stopwatch.Stop()-then-log ordering).
            var startTimestamp = Stopwatch.GetTimestamp();
            try
            {
                var result = await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp);

                if (result is Shared.Abstractions.Result { IsFailure: true } failureResult)
                {
                    var errorSummary = string.Join("; ", failureResult.Errors.Select(e => $"{e.Code}: {e.Message}"));
                    LogCommandFailed(logger, commandName, (long)elapsed.TotalMilliseconds, correlationId, errorSummary);
                    RecordDuration(commandName, elapsed, "failed");
                }
                else
                {
                    LogCommandCompleted(logger, commandName, (long)elapsed.TotalMilliseconds, correlationId);
                    RecordDuration(commandName, elapsed, "completed");
                }

                return result;
            }
            catch (Exception ex)
            {
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                LogCommandException(logger, commandName, (long)elapsed.TotalMilliseconds, correlationId, ex);
                RecordDuration(commandName, elapsed, "exception");
                throw;
            }
        }
    }

    /// <summary>
    /// Source-generated scope: avoids the per-command dictionary/boxing allocation of an
    /// anonymous <c>BeginScope</c> payload while keeping the same structured keys.
    /// </summary>
    private static readonly Func<ILogger, string, string, string, IDisposable?> BeginCommandScope =
        LoggerMessage.DefineScope<string, string, string>(
            "Command {CommandName} [Module: {ModuleName}] [CorrelationId: {CorrelationId}]");

    /// <summary>
    /// Owning module of the command, derived once per closed generic type (the static field is
    /// per <c>TCommand</c>), so every log line a handler emits can be filtered by module without
    /// paying for the namespace parse per execution.
    /// </summary>
    private static readonly string ModuleName = ModuleNameConventions.GetModuleName(typeof(TCommand)) ?? "unknown";

    private static void RecordDuration(string commandName, TimeSpan elapsed, string outcome) =>
        CqrsMetrics.CommandDuration.Record(
            elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("command", commandName),
            new KeyValuePair<string, object?>("outcome", outcome));

    // Started is Debug: the completion line already carries the name and duration, and two
    // Information rows per command doubles ingestion cost for no diagnostic gain.
    [LoggerMessage(Level = LogLevel.Debug, Message = "Executing command {CommandName} [CorrelationId: {CorrelationId}]")]
    private static partial void LogCommandStarted(ILogger logger, string commandName, string correlationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {CommandName} completed in {ElapsedMs}ms [CorrelationId: {CorrelationId}]")]
    private static partial void LogCommandCompleted(ILogger logger, string commandName, long elapsedMs, string correlationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command {CommandName} failed after {ElapsedMs}ms [CorrelationId: {CorrelationId}] — {ErrorSummary}")]
    private static partial void LogCommandFailed(ILogger logger, string commandName, long elapsedMs, string correlationId, string errorSummary);

    [LoggerMessage(Level = LogLevel.Error, Message = "Command {CommandName} threw after {ElapsedMs}ms [CorrelationId: {CorrelationId}]")]
    private static partial void LogCommandException(ILogger logger, string commandName, long elapsedMs, string correlationId, Exception exception);
}
