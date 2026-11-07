using Microsoft.Extensions.Options;

namespace MMCA.Common.UI.Services.Diagnostics;

/// <summary>
/// A bounded, thread-safe, in-memory ring buffer of log entries, filled by
/// <see cref="InAppLoggerProvider"/> and read by the <c>InAppDiagnosticsLog</c> component. It lives
/// only in process memory: nothing is written to disk or sent anywhere, and the entries are gone
/// when the app restarts.
/// </summary>
public sealed class InAppLogBuffer
{
    private readonly Lock _gate = new();
    private readonly InAppLogEntry[] _entries;
    private int _next;
    private int _count;

    /// <summary>Initializes a new instance of the <see cref="InAppLogBuffer"/> class.</summary>
    /// <param name="options">The diagnostics options; <see cref="InAppDiagnosticsOptions.Capacity"/> sizes the buffer.</param>
    public InAppLogBuffer(IOptions<InAppDiagnosticsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var capacity = options.Value.Capacity;
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _entries = new InAppLogEntry[capacity];
    }

    /// <summary>Gets the number of entries the buffer keeps before it drops the oldest.</summary>
    public int Capacity => _entries.Length;

    /// <summary>Gets the number of entries currently held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Appends an entry, dropping the oldest one when the buffer is full.</summary>
    /// <param name="entry">The entry to append.</param>
    public void Add(InAppLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            _entries[_next] = entry;
            _next = (_next + 1) % _entries.Length;
            if (_count < _entries.Length)
            {
                _count++;
            }
        }
    }

    /// <summary>Returns a snapshot of the held entries, oldest first and newest last.</summary>
    /// <returns>The entries in the order they were written.</returns>
    public IReadOnlyList<InAppLogEntry> Snapshot()
    {
        lock (_gate)
        {
            var snapshot = new InAppLogEntry[_count];
            var start = (_next - _count + _entries.Length) % _entries.Length;
            for (var i = 0; i < _count; i++)
            {
                snapshot[i] = _entries[(start + i) % _entries.Length];
            }

            return snapshot;
        }
    }

    /// <summary>Removes every entry.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_entries);
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>Formats the held entries as text, one entry per line, newest last.</summary>
    /// <returns>The log as text, or an empty string when the buffer is empty.</returns>
    public string ToText() => string.Join(Environment.NewLine, Snapshot());
}
