using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Infrastructure.Tests.TestDoubles;

/// <summary>
/// A logger that keeps every entry together with its STRUCTURED values (the named placeholders a
/// source-generated <c>LoggerMessage</c> passes as its state), so a test can assert what a line carries
/// rather than how its message reads. It can also be awaited until a number of entries has arrived,
/// which is how a test observes the lines a background service writes when it starts without sleeping.
/// </summary>
/// <typeparam name="T">The category type.</typeparam>
internal sealed class StructuredRecordingLogger<T> : ILogger<T>
{
    private const string OriginalFormatKey = "{OriginalFormat}";

    private readonly ConcurrentQueue<Entry> _entries = new();
    private readonly System.Threading.Lock _gate = new();
    private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];

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

        var values = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
            ? pairs
                .Where(pair => !string.Equals(pair.Key, OriginalFormatKey, StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        _entries.Enqueue(new Entry(logLevel, eventId.Name, formatter(state, exception), exception, values));

        lock (_gate)
        {
            var written = _entries.Count;
            foreach (var (_, reached) in _waiters.Where(waiter => written >= waiter.Count))
            {
                reached.TrySetResult();
            }
        }
    }

    /// <summary>Completes once at least <paramref name="count"/> entries have been written.</summary>
    /// <param name="count">The number of entries to wait for.</param>
    /// <returns>A task that completes when the count is reached.</returns>
    public Task WhenCountAsync(int count)
    {
        lock (_gate)
        {
            if (_entries.Count >= count)
            {
                return Task.CompletedTask;
            }

            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, reached));
            return reached.Task;
        }
    }

    /// <summary>One written log line with its structured values.</summary>
    /// <param name="Level">The level.</param>
    /// <param name="EventName">The source-generated event name (the LoggerMessage method name).</param>
    /// <param name="Message">The rendered message.</param>
    /// <param name="Exception">The attached exception, if any.</param>
    /// <param name="Values">The named placeholder values, without the original format string.</param>
    internal sealed record Entry(
        LogLevel Level,
        string? EventName,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Values)
    {
        /// <summary>Whether a structured value is the number <paramref name="expected"/> (a TimeSpan counts in seconds).</summary>
        /// <param name="expected">The number to look for.</param>
        /// <returns><see langword="true"/> when a value matches.</returns>
        public bool CarriesNumber(double expected) =>
            Values.Values.Any(value => value switch
            {
                TimeSpan span => Math.Abs(span.TotalSeconds - expected) < 0.001,
                byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
                    Math.Abs(Convert.ToDouble(value, CultureInfo.InvariantCulture) - expected) < 0.001,
                _ => false,
            });

        /// <summary>Whether a structured value renders exactly as <paramref name="expected"/>.</summary>
        /// <param name="expected">The text to look for.</param>
        /// <returns><see langword="true"/> when a value matches.</returns>
        public bool CarriesText(string expected) =>
            Values.Values.Any(value => string.Equals(
                Convert.ToString(value, CultureInfo.InvariantCulture), expected, StringComparison.Ordinal));

        /// <summary>Whether the message or any structured value contains <paramref name="fragment"/>.</summary>
        /// <param name="fragment">The text to look for.</param>
        /// <returns><see langword="true"/> when the fragment appears anywhere in the line.</returns>
        public bool Mentions(string fragment) =>
            Message.Contains(fragment, StringComparison.Ordinal)
            || Values.Values.Any(value =>
                Convert.ToString(value, CultureInfo.InvariantCulture)?.Contains(fragment, StringComparison.Ordinal) == true);
    }
}
