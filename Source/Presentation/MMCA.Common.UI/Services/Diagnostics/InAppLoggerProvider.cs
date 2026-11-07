using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MMCA.Common.UI.Services.Diagnostics;

/// <summary>
/// An <see cref="ILoggerProvider"/> that records every log entry at or above
/// <see cref="InAppDiagnosticsOptions.MinimumLevel"/> into the in-memory <see cref="InAppLogBuffer"/>,
/// so a store build of an app (where the Debug logger is compiled out) still has a log the user can
/// read, copy and share. Registered by <c>AddInAppDiagnostics</c>; nothing is written to disk or
/// sent anywhere. Messages are kept as formatted, and an exception is kept as its type and message.
/// </summary>
public sealed class InAppLoggerProvider : ILoggerProvider
{
    private readonly InAppLogBuffer _buffer;
    private readonly TimeProvider _timeProvider;
    private readonly LogLevel _minimumLevel;

    /// <summary>Initializes a new instance of the <see cref="InAppLoggerProvider"/> class.</summary>
    /// <param name="buffer">The buffer entries are written to.</param>
    /// <param name="options">The diagnostics options; supplies the minimum level.</param>
    /// <param name="timeProvider">The clock entries are stamped with.</param>
    public InAppLoggerProvider(InAppLogBuffer buffer, IOptions<InAppDiagnosticsOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _buffer = buffer;
        _timeProvider = timeProvider;
        _minimumLevel = options.Value.MinimumLevel;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new InAppLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        // The buffer is a DI singleton of its own and outlives the provider; nothing to release.
    }

    private sealed class InAppLogger(InAppLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= provider._minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider._buffer.Add(new InAppLogEntry(
                provider._timeProvider.GetUtcNow(),
                logLevel,
                categoryName,
                formatter(state, exception),
                exception is null ? null : DescribeException(exception)));
        }

        private static string DescribeException(Exception exception)
        {
            var text = new StringBuilder();
            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (text.Length > 0)
                {
                    text.Append(" ---> ");
                }

                text.Append(current.GetType().FullName).Append(": ").Append(current.Message);
            }

            return text.ToString();
        }
    }
}
