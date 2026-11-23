using System.Collections.Concurrent;
using System.Linq.Expressions;
using MMCA.Common.Application.Interfaces.Events;

namespace MMCA.Common.Infrastructure.Messaging.Consumers;

/// <summary>
/// Caches the closed handler interface and its compiled invoker per terminal event type for
/// <see cref="UpcastingIntegrationEventConsumer{TEvent}"/>. The terminal type is only known at runtime
/// (it depends on which upcasters the host registered), so handler resolution is non-generic; the
/// compiled expression keeps reflection off the per-message path exactly as
/// <c>DomainEventDispatcher</c> does for the in-process path. Non-generic on purpose, so every closed
/// consumer type shares one cache.
/// </summary>
internal static class UpcastDispatch
{
    private static readonly ConcurrentDictionary<
        Type,
        (Type ClosedHandlerType, Func<object, object, CancellationToken, Task> Invoker)> Cache = new();

    /// <summary>Gets the closed handler type and invoker for <paramref name="terminalType"/>.</summary>
    /// <param name="terminalType">The terminal event type the handlers are registered for.</param>
    /// <returns>The closed <c>IIntegrationEventHandler&lt;T&gt;</c> type and its compiled invoker.</returns>
    internal static (Type ClosedHandlerType, Func<object, object, CancellationToken, Task> Invoker) For(Type terminalType) =>
        Cache.GetOrAdd(
            terminalType,
            static eventType => (
                typeof(IIntegrationEventHandler<>).MakeGenericType(eventType),
                BuildInvoker(eventType)));

    /// <summary>
    /// Builds a compiled delegate that invokes <c>HandleAsync</c> on
    /// <c>IIntegrationEventHandler&lt;TTerminal&gt;</c> without boxing or reflection at call time. The
    /// expression tree casts the <see langword="object"/> parameters to their concrete types, then
    /// calls the strongly-typed method directly.
    /// </summary>
    /// <param name="eventType">The terminal event type to build the invoker for.</param>
    /// <returns>A delegate that accepts (handler, event, cancellationToken) as objects.</returns>
    private static Func<object, object, CancellationToken, Task> BuildInvoker(Type eventType)
    {
        var closedHandlerType = typeof(IIntegrationEventHandler<>).MakeGenericType(eventType);
        var method = closedHandlerType.GetMethod("HandleAsync")
            ?? throw new InvalidOperationException($"HandleAsync method not found on {closedHandlerType.Name}");

        // Build: (object handler, object integrationEvent, CancellationToken ct) =>
        //     ((IIntegrationEventHandler<TEvent>)handler).HandleAsync((TEvent)integrationEvent, ct)
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var eventParam = Expression.Parameter(typeof(object), "integrationEvent");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        var call = Expression.Call(
            Expression.Convert(handlerParam, closedHandlerType),
            method,
            Expression.Convert(eventParam, eventType),
            ctParam);

        return Expression.Lambda<Func<object, object, CancellationToken, Task>>(
            call, handlerParam, eventParam, ctParam).Compile();
    }
}
