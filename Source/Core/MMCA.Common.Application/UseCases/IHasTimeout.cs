namespace MMCA.Common.Application.UseCases;

/// <summary>
/// Marker interface for commands and queries that carry their own execution budget.
/// The <see cref="Decorators.TimeoutCommandDecorator{TCommand,TResult}"/> and
/// <see cref="Decorators.TimeoutQueryDecorator{TQuery,TResult}"/> link a cancellation source to the
/// caller's token, cancel it after <see cref="Timeout"/>, and convert the resulting cancellation
/// into a failure result rather than letting it surface as an exception.
/// <para>
/// Opting in is per request type: a command or query that does not implement this interface passes
/// through the decorator untouched and keeps the caller's token unchanged.
/// </para>
/// </summary>
public interface IHasTimeout
{
    /// <summary>
    /// The execution budget for this request. A value less than or equal to
    /// <see cref="TimeSpan.Zero"/> disables the budget and passes the request through with the
    /// caller's token, which keeps a misconfigured value from failing every request instantly.
    /// </summary>
    TimeSpan Timeout { get; }
}
