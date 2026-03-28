using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Services;

/// <summary>
/// Dispatches domain events to all registered <see cref="IDomainEventHandler{T}"/> instances,
/// and integration events to all registered <see cref="IIntegrationEventHandler{T}"/> instances.
/// Uses compiled expression trees cached per event type to avoid repeated reflection
/// when invoking the generic <c>HandleAsync</c> method on each handler.
/// </summary>
public sealed class DomainEventDispatcher(IServiceProvider serviceProvider, ILogger<DomainEventDispatcher> logger) : IDomainEventDispatcher
{
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    /// <summary>
    /// Caches compiled delegates keyed by (eventType, handlerInterfaceType).
    /// Each entry compiles a single expression tree that invokes <c>HandleAsync</c> on
    /// the target handler interface without boxing or reflection at call time.
    /// </summary>
    private static readonly ConcurrentDictionary<(Type EventType, Type HandlerInterface), Func<object, object, CancellationToken, Task>> CompiledDelegates = new();

    /// <inheritdoc />
    public async Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        foreach (var domainEvent in domainEvents)
        {
            var eventType = domainEvent.GetType();

            // Always dispatch to IDomainEventHandler<T> (intra-module handlers).
            await DispatchToHandlersAsync(eventType, typeof(IDomainEventHandler<>), domainEvent, cancellationToken).ConfigureAwait(false);

            // If the event also implements IIntegrationEvent, dispatch to IIntegrationEventHandler<T> (cross-module handlers).
            if (domainEvent is IIntegrationEvent)
                await DispatchToHandlersAsync(eventType, typeof(IIntegrationEventHandler<>), domainEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchToHandlersAsync(Type eventType, Type openHandlerType, IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var closedHandlerType = openHandlerType.MakeGenericType(eventType);
        var handlers = _serviceProvider.GetServices(closedHandlerType);
        var invoker = CompiledDelegates.GetOrAdd(
            (eventType, openHandlerType),
            static key => BuildInvoker(key.EventType, key.HandlerInterface));

        foreach (var handler in handlers)
        {
            if (handler is null)
            {
                logger.LogWarning("Null handler resolved for event type {EventType} via {HandlerType} — possible DI misconfiguration", eventType.Name, openHandlerType.Name);
                continue;
            }

            await invoker(handler, domainEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds a compiled delegate that invokes <c>HandleAsync</c> on a handler interface
    /// without boxing or reflection at call time. The expression tree casts the
    /// <see langword="object"/> parameters to their concrete types, then calls the
    /// strongly-typed method directly.
    /// </summary>
    /// <param name="eventType">The concrete event type to build the invoker for.</param>
    /// <param name="openHandlerType">The open generic handler interface (e.g., <c>IDomainEventHandler&lt;&gt;</c>).</param>
    /// <returns>A delegate that accepts (handler, event, cancellationToken) as objects.</returns>
    private static Func<object, object, CancellationToken, Task> BuildInvoker(Type eventType, Type openHandlerType)
    {
        var closedHandlerType = openHandlerType.MakeGenericType(eventType);
        var method = closedHandlerType.GetMethod("HandleAsync")
            ?? throw new InvalidOperationException($"HandleAsync method not found on {closedHandlerType.Name}");

        // Build: (object handler, object event, CancellationToken ct) =>
        //     ((IHandler<TEvent>)handler).HandleAsync((TEvent)event, ct)
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var eventParam = Expression.Parameter(typeof(object), "domainEvent");
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
