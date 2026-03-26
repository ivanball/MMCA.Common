namespace MMCA.Common.Application.UseCases;

/// <summary>
/// Handles a command (mutation) and returns a result. Implementations are auto-registered
/// via Scrutor and wrapped by decorators (transactional, caching, profiling).
/// </summary>
/// <typeparam name="TCommand">The command type containing the mutation parameters.</typeparam>
/// <typeparam name="TResult">The result type (typically <c>Result</c> or <c>Result&lt;T&gt;</c>).</typeparam>
public interface ICommandHandler<in TCommand, TResult>
{
    /// <summary>
    /// Executes the command.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the command execution.</returns>
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}
