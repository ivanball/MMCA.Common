using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

/// <summary>
/// Runs one deserialized command through the ordinary CQRS pipeline by resolving
/// <c>ICommandHandler&lt;TCommand, Result&gt;</c> from a scope.
/// <para>
/// Resolving the CLOSED handler interface is the whole point: Scrutor's decorators are registered
/// against the open <c>ICommandHandler&lt;,&gt;</c>, so whatever comes back from the container is
/// already wrapped in the feature gate, the authorization check, logging, cache invalidation,
/// validation, the timeout budget and the transaction. A deferred execution is therefore the same
/// execution a controller would have got, not a parallel path that has to re-implement any of it.
/// </para>
/// </summary>
internal static class InternalCommandDispatcher
{
    /// <summary>
    /// One compiled invoker per command type. Building it costs a
    /// <see cref="MethodInfo.MakeGenericMethod"/> and a delegate creation; the queue would otherwise
    /// pay both plus a <c>MethodInfo.Invoke</c> on every row it executes.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, Func<IServiceProvider, IInternalCommand, CancellationToken, Task<Result?>>> Invokers = new();

#pragma warning disable S3011 // The target is this same class's own private generic method, looked up by nameof so a rename cannot silently break it. Nothing external is reached and no accessibility boundary of another type is crossed; the alternative would be widening the method's visibility purely to satisfy the rule.
    private static readonly MethodInfo InvokeDefinition =
        typeof(InternalCommandDispatcher).GetMethod(nameof(InvokeAsync), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("InternalCommandDispatcher.InvokeAsync is missing.");
#pragma warning restore S3011

    /// <summary>
    /// Executes <paramref name="command"/> using the handler registered in
    /// <paramref name="serviceProvider"/>.
    /// </summary>
    /// <param name="serviceProvider">The execution scope's provider.</param>
    /// <param name="command">The deserialized command.</param>
    /// <param name="cancellationToken">Cancellation token, cancelled on host shutdown.</param>
    /// <returns>
    /// The handler's result, or <see langword="null"/> when this host registers no handler for the
    /// command. A missing handler is reported rather than thrown, because it is a deployment fact
    /// (the module that owns the command is disabled, or the row was written by a different service)
    /// and the caller records it on the row.
    /// </returns>
    internal static Task<Result?> ExecuteAsync(
        IServiceProvider serviceProvider,
        IInternalCommand command,
        CancellationToken cancellationToken)
    {
        var invoker = Invokers.GetOrAdd(command.GetType(), BuildInvoker);
        return invoker(serviceProvider, command, cancellationToken);
    }

    private static Func<IServiceProvider, IInternalCommand, CancellationToken, Task<Result?>> BuildInvoker(
        Type commandType) =>
        InvokeDefinition
            .MakeGenericMethod(commandType)
            .CreateDelegate<Func<IServiceProvider, IInternalCommand, CancellationToken, Task<Result?>>>();

    /// <summary>
    /// The generic body the invoker closes over: resolve the decorated handler for one command type
    /// and call it. <c>GetService</c> rather than <c>GetRequiredService</c>, so an unhandled command
    /// reads as a null result the processor can record instead of an exception it has to classify.
    /// </summary>
    private static async Task<Result?> InvokeAsync<TCommand>(
        IServiceProvider serviceProvider,
        IInternalCommand command,
        CancellationToken cancellationToken)
        where TCommand : IInternalCommand
    {
        var handler = serviceProvider.GetService<ICommandHandler<TCommand, Result>>();
        if (handler is null)
        {
            return null;
        }

        return await handler.HandleAsync((TCommand)command, cancellationToken).ConfigureAwait(false);
    }
}
