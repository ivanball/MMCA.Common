using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MMCA.Common.Infrastructure.Tests.TestDoubles;

/// <summary>
/// Records the text of every SQL command one <see cref="DbContext"/> executes, through EF Core's
/// diagnostic listener rather than an interceptor, so it observes a context whose options are already
/// fixed. Commands of any other context in the process are ignored, which keeps it safe under the
/// parallel test run.
/// </summary>
internal sealed class EfCommandRecorder :
    IObserver<DiagnosticListener>,
    IObserver<KeyValuePair<string, object?>>,
    IDisposable
{
    private readonly DbContext _context;
    private readonly ConcurrentQueue<string> _commands = new();
    private readonly System.Threading.Lock _gate = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly IDisposable _allListeners;

    /// <summary>Starts recording the commands <paramref name="context"/> executes.</summary>
    /// <param name="context">The context to observe.</param>
    public EfCommandRecorder(DbContext context)
    {
        _context = context;
        _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
    }

    /// <summary>Gets the command texts in execution order.</summary>
    public IReadOnlyList<string> Commands => [.. _commands];

    /// <summary>Whether a command is an UPDATE statement.</summary>
    /// <param name="sql">The command text.</param>
    /// <returns><see langword="true"/> for an UPDATE.</returns>
    public static bool IsUpdate(string sql) =>
        sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The part of a statement from its first <c>WHERE</c> on. A SELECT projects every column, so the
    /// predicate is the only place a column name tells us anything about which rows are read.
    /// </summary>
    /// <param name="sql">The command text.</param>
    /// <returns>The WHERE clause and what follows it, or an empty string when there is none.</returns>
    public static string WhereClause(string sql)
    {
        var index = sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? string.Empty : sql[index..];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _allListeners.Dispose();
        lock (_gate)
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener value)
    {
        if (string.Equals(value.Name, DbLoggerCategory.Name, StringComparison.Ordinal))
        {
            lock (_gate)
            {
                _subscriptions.Add(value.Subscribe(this));
            }
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
    {
        if (string.Equals(value.Key, RelationalEventId.CommandExecuting.Name, StringComparison.Ordinal)
            && value.Value is CommandEventData data
            && ReferenceEquals(data.Context, _context))
        {
            _commands.Enqueue(data.Command.CommandText);
        }
    }

    void IObserver<DiagnosticListener>.OnCompleted()
    {
        // The set of listeners never completes while the process runs; nothing to record.
    }

    void IObserver<DiagnosticListener>.OnError(Exception error)
    {
        // The listener enumeration does not report errors; nothing to record.
    }

    void IObserver<KeyValuePair<string, object?>>.OnCompleted()
    {
        // EF's listener lives as long as its internal service provider; nothing to record.
    }

    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error)
    {
        // EF never reports errors through the listener itself; failed commands arrive as events.
    }
}
