using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Services;

/// <summary>
/// Dispatches domain events to all registered <see cref="IDomainEventHandler{T}"/> instances.
/// Uses compiled expression trees cached per event type to avoid repeated reflection
/// when invoking the generic <c>HandleAsync</c> method on each handler.
/// </summary>
public sealed class DomainEventDispatcher(IServiceProvider serviceProvider, ILogger<DomainEventDispatcher> logger) : IDomainEventDispatcher
{
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    /// <summary>
    /// Caches a compiled delegate per domain event type. Because the handler interface is
    /// open-generic (<c>IDomainEventHandler&lt;&gt;</c>), we cannot call HandleAsync without
    /// reflection or expression compilation. Caching eliminates the per-dispatch overhead.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task>> CompiledDelegates = new();

    /// <inheritdoc />
    public async Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        foreach (var domainEvent in domainEvents)
        {
            var eventType = domainEvent.GetType();
            var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(eventType);
            var handlers = _serviceProvider.GetServices(handlerType);
            var invoker = CompiledDelegates.GetOrAdd(eventType, static et => BuildInvoker(et));

            foreach (var handler in handlers)
            {
                if (handler is null)
                {
                    logger.LogWarning("Null handler resolved for domain event type {EventType} — possible DI misconfiguration", eventType.Name);
                    continue;
                }

                await invoker(handler, domainEvent, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Builds a compiled delegate that invokes <c>HandleAsync</c> on an
    /// <see cref="IDomainEventHandler{T}"/> without boxing or reflection at call time.
    /// The expression tree casts the <see langword="object"/> parameters to their concrete types,
    /// then calls the strongly-typed method directly.
    /// </summary>
    /// <param name="eventType">The concrete domain event type to build the invoker for.</param>
    /// <returns>A delegate that accepts (handler, domainEvent, cancellationToken) as objects.</returns>
    private static Func<object, object, CancellationToken, Task> BuildInvoker(Type eventType)
    {
        var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(eventType);
        var method = handlerType.GetMethod(nameof(IDomainEventHandler<>.HandleAsync))
            ?? throw new InvalidOperationException($"HandleAsync method not found on {handlerType.Name}");

        // Build: (object handler, object domainEvent, CancellationToken ct) =>
        //     ((IDomainEventHandler<TEvent>)handler).HandleAsync((TEvent)domainEvent, ct)
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var eventParam = Expression.Parameter(typeof(object), "domainEvent");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        var call = Expression.Call(
            Expression.Convert(handlerParam, handlerType),
            method,
            Expression.Convert(eventParam, eventType),
            ctParam);

        return Expression.Lambda<Func<object, object, CancellationToken, Task>>(
            call, handlerParam, eventParam, ctParam).Compile();
    }
}
