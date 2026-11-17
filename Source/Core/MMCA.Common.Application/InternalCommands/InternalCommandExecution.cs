namespace MMCA.Common.Application.InternalCommands;

/// <summary>
/// Ambient marker for "the current flow is executing a scheduled internal command" (ADR-114), so a
/// handler that one command type serves on two paths can tell the durable path from an inline one
/// without a second command type or a payload flag.
/// <para>
/// The value flows with the <see cref="ExecutionContext"/> (an <see cref="AsyncLocal{T}"/>), the same
/// mechanism <c>QueryTagScope</c> uses. The internal-command processor opens the scope around each
/// dispatch; nothing else should. A command dispatched inline from inside a running internal command
/// inherits the marker, which is correct: its failure fails the row that is running.
/// </para>
/// <para>
/// The case it exists for: an anonymous HTTP endpoint must answer success whatever happens
/// (anti-enumeration), while the same command scheduled as an internal command must report a
/// transient failure so the row is retried instead of acknowledged.
/// <c>SendEmailConfirmationHandlerBase</c> reads it to propagate an SMTP failure only on the durable
/// path.
/// </para>
/// </summary>
public static class InternalCommandExecution
{
    private static readonly AsyncLocal<bool> Active = new();

    /// <summary>
    /// Gets a value indicating whether the current flow is executing a scheduled internal command.
    /// <see langword="false"/> for a request, a background service or a test that dispatches a handler
    /// directly.
    /// </summary>
    public static bool IsActive => Active.Value;

    /// <summary>
    /// Marks the current flow as executing a scheduled internal command until the handle is disposed.
    /// Dispose restores the previous value, so nested scopes unwind correctly. Called by the
    /// internal-command processor around each dispatch.
    /// </summary>
    /// <returns>A handle that restores the previous value when disposed.</returns>
    public static IDisposable Begin()
    {
        var previous = Active.Value;
        Active.Value = true;
        return previous ? NestedScope : OutermostScope;
    }

    private static readonly IDisposable OutermostScope = new Scope(previous: false);

    private static readonly IDisposable NestedScope = new Scope(previous: true);

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Active.Value = previous;
    }
}
