using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Architecture.Tests.PayloadPurityFixtures.IntegrationEvents;
using MMCA.Common.Architecture.Tests.RuleShapeFixtures.DomainEvents;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Architecture.Tests.RuleShapeFixtures;

/// <summary>
/// Compiled handler and service shapes for the placement, injection, arity and suffix rules
/// (<c>HandlerPlacementRuleTests</c>, <c>NamingRuleTests</c>): one type per way of satisfying or
/// breaking each rule. Every message is payload-free, so the command-validator inventory that scans
/// this same assembly (it counts data-carrying commands only) is unaffected. Unsealed shapes are
/// public, because an unsealed internal type is itself an analyzer error.
/// </summary>
public sealed record ShapePlaceOrderCommand;

/// <summary>A command message carrying the accepted "Request" suffix.</summary>
public sealed record ShapeRenameOrderRequest;

/// <summary>A command message with neither accepted suffix.</summary>
public sealed record ShapeCancelOrderMessage;

/// <summary>A query message with the "Query" suffix.</summary>
public sealed record ShapeOrderTotalQuery;

/// <summary>A query message without the "Query" suffix.</summary>
public sealed record ShapeOrderTotalFetch;

/// <summary>Compliant: sealed, "Handler" suffix, no injected handler.</summary>
public sealed class ShapePlaceOrderHandler : ICommandHandler<ShapePlaceOrderCommand, Result>
{
    public Task<Result> HandleAsync(ShapePlaceOrderCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

/// <summary>Compliant handler for the "Request"-suffixed command.</summary>
public sealed class ShapeRenameOrderHandler : ICommandHandler<ShapeRenameOrderRequest, Result>
{
    public Task<Result> HandleAsync(ShapeRenameOrderRequest command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

/// <summary>Compliant query handler for the "Query"-suffixed query.</summary>
public sealed class ShapeOrderTotalHandler : IQueryHandler<ShapeOrderTotalQuery, Result>
{
    public Task<Result> HandleAsync(ShapeOrderTotalQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

/// <summary>
/// Handles the badly-suffixed command through TWO closed interfaces, so the suffix rule sees the same
/// (message, handler) pair twice and must still report it once.
/// </summary>
public sealed class ShapeCancelOrderHandler
    : ICommandHandler<ShapeCancelOrderMessage, Result>, ICommandHandler<ShapeCancelOrderMessage, int>
{
    public Task<Result> HandleAsync(ShapeCancelOrderMessage command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());

    Task<int> ICommandHandler<ShapeCancelOrderMessage, int>.HandleAsync(ShapeCancelOrderMessage command, CancellationToken cancellationToken) =>
        Task.FromResult(0);
}

/// <summary>Handles the query whose name lacks the "Query" suffix.</summary>
public sealed class ShapeOrderFetchHandler : IQueryHandler<ShapeOrderTotalFetch, Result>
{
    public Task<Result> HandleAsync(ShapeOrderTotalFetch query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

/// <summary>Violation: a handler that is not sealed.</summary>
public class ShapeUnsealedOrderHandler : ICommandHandler<ShapePlaceOrderCommand, Result>
{
    public Task<Result> HandleAsync(ShapePlaceOrderCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

/// <summary>Violation: a sealed handler whose name does not end in "Handler".</summary>
public sealed class ShapeOrderProcessor : ICommandHandler<ShapePlaceOrderCommand, Result>
{
    public Task<Result> HandleAsync(ShapePlaceOrderCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

/// <summary>Not a handler at all, so neither the sealing nor the suffix rule reads it.</summary>
public class ShapeOpenNotificationHandler
{
    public string Channel { get; } = "email";
}

/// <summary>Violation: a handler composing another handler through its interface.</summary>
public sealed class ShapeComposingHandler(ICommandHandler<ShapePlaceOrderCommand, Result> inner)
    : ICommandHandler<ShapeRenameOrderRequest, Result>
{
    public Task<Result> HandleAsync(ShapeRenameOrderRequest command, CancellationToken cancellationToken = default) =>
        inner.HandleAsync(new ShapePlaceOrderCommand(), cancellationToken);
}

/// <summary>Violation: a handler composing another handler through its concrete type.</summary>
public sealed class ShapeConcreteComposingHandler(ShapePlaceOrderHandler inner)
    : ICommandHandler<ShapeRenameOrderRequest, Result>
{
    public Task<Result> HandleAsync(ShapeRenameOrderRequest command, CancellationToken cancellationToken = default) =>
        inner.HandleAsync(new ShapePlaceOrderCommand(), cancellationToken);
}

/// <summary>Violation: a non-handler service brokering a query through the handler interface.</summary>
public sealed class ShapeOrderBrokerService(IQueryHandler<ShapeOrderTotalQuery, Result> totals)
{
    public Task<Result> TotalAsync(CancellationToken cancellationToken) =>
        totals.HandleAsync(new ShapeOrderTotalQuery(), cancellationToken);
}

/// <summary>Violation: a non-handler type brokering a command through a concrete handler.</summary>
public sealed class ShapeOrderRelay(ShapePlaceOrderHandler handler)
{
    public Task<Result> RelayAsync(CancellationToken cancellationToken) =>
        handler.HandleAsync(new ShapePlaceOrderCommand(), cancellationToken);
}

/// <summary>Compliant: a service at exactly the arity ceiling the tests pass (2), with no handler dependency.</summary>
public sealed class ShapeOrderPricingService(string currency, string region)
{
    public string Describe() => string.Concat(currency, region);
}

/// <summary>Violation at a ceiling of 2: three constructor dependencies.</summary>
public sealed class ShapeOrderWideService(string currency, string region, string channel)
{
    public string Describe() => string.Concat(currency, region, channel);
}

/// <summary>Two public constructors: the arity rule reads the widest one (three).</summary>
public sealed class ShapeOrderOverloadedService
{
    public ShapeOrderOverloadedService(string currency) => Description = currency;

    public ShapeOrderOverloadedService(string currency, string region, string channel) =>
        Description = string.Concat(currency, region, channel);

    public string Description { get; }
}

/// <summary>A service with no public constructor: nothing for the arity rule to measure.</summary>
public sealed class ShapeOrderHiddenService
{
    private ShapeOrderHiddenService()
    {
    }

    public static ShapeOrderHiddenService Create() => new();
}

/// <summary>Wide, but not a "*Service", so the arity rule does not read it.</summary>
public sealed class ShapeOrderWideHelper(string currency, string region, string channel, string locale)
{
    public string Describe() => string.Concat(currency, region, channel, locale);
}

/// <summary>Compliant domain event handler: sealed.</summary>
public sealed class ShapeOrderPlacedHandler : IDomainEventHandler<ShapeOrderPlaced>
{
    public Task HandleAsync(ShapeOrderPlaced domainEvent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>Violation: an unsealed domain event handler.</summary>
public class ShapeUnsealedOrderPlacedHandler : IDomainEventHandler<ShapeOrderPlaced>
{
    public Task HandleAsync(ShapeOrderPlaced domainEvent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>A sealed integration event handler: compliant in Application, misplaced in Domain.</summary>
internal sealed class ShapeCleanEventConsumer : IIntegrationEventHandler<FixtureCleanEvent>
{
    public Task HandleAsync(FixtureCleanEvent integrationEvent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
