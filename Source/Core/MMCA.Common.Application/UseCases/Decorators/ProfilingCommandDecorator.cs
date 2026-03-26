using StackExchange.Profiling;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that wraps command handler execution in a MiniProfiler step,
/// enabling performance tracing for each command type.
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
public sealed class ProfilingCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner) : ICommandHandler<TCommand, TResult>
{
    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        using var step = MiniProfiler.Current?.Step($"CommandHandler: {typeof(TCommand).Name}");
        return await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);
    }
}
