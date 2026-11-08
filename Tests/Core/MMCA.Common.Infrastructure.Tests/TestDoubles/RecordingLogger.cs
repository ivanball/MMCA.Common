using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Infrastructure.Tests.TestDoubles;

/// <summary>
/// A logger that keeps every entry it receives (level, event name, rendered message, exception), so a
/// test can assert which line was written and whether it carried the exception object. Whether an
/// exception is attached decides the App Insights table a line lands in (AppTraces or AppExceptions),
/// which is what an alert query reads.
/// </summary>
/// <typeparam name="T">The category type.</typeparam>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<Entry> _entries = new();

    /// <summary>Gets the entries in the order they were written.</summary>
    public IReadOnlyList<Entry> Entries => [.. _entries];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _entries.Enqueue(new Entry(logLevel, eventId.Name, formatter(state, exception), exception));
    }

    /// <summary>One written log line.</summary>
    /// <param name="Level">The level.</param>
    /// <param name="EventName">The source-generated event name (the LoggerMessage method name).</param>
    /// <param name="Message">The rendered message.</param>
    /// <param name="Exception">The attached exception, if any.</param>
    internal sealed record Entry(LogLevel Level, string? EventName, string Message, Exception? Exception);
}
