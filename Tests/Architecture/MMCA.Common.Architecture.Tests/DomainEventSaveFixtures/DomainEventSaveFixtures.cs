using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Architecture.Tests.DomainEventSaveFixtures;

/// <summary>
/// Compiled call sites for <c>DomainEventHandlerSaveFitnessTests</c>. The rule walks IL, so the only
/// honest way to test it is to compile the handler shapes it must (and must not) flag into this
/// assembly and point a map at it: a direct save, a two-hop save through a concrete service, a save
/// behind an interface, and a handler that only mutates.
/// </summary>
public sealed record FixtureDomainEvent(DateTime DateOccurred, Guid MessageId) : IDomainEvent;

/// <summary>Saves in the handler itself. Caught at the call site, no traversal needed.</summary>
internal sealed class DirectSavingHandler : IDomainEventHandler<FixtureDomainEvent>
{
    private readonly IUnitOfWork _unitOfWork;

    internal DirectSavingHandler(IUnitOfWork unitOfWork) => _unitOfWork = unitOfWork;

    public Task HandleAsync(FixtureDomainEvent domainEvent, CancellationToken cancellationToken = default) =>
        _unitOfWork.SaveChangesAsync(cancellationToken);
}

/// <summary>
/// The real-world shape: the handler delegates to a service, which delegates to a writer, which
/// saves. Two hops, both through <c>async</c> methods, so the walk must follow the compiler-generated
/// state machines to see anything at all.
/// </summary>
internal sealed class TransitiveSavingHandler : IDomainEventHandler<FixtureDomainEvent>
{
    private readonly PointsAwarder _awarder;

    internal TransitiveSavingHandler(PointsAwarder awarder) => _awarder = awarder;

    public async Task HandleAsync(FixtureDomainEvent domainEvent, CancellationToken cancellationToken = default) =>
        await _awarder.AwardAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>Hop one of the transitive chain: no save here.</summary>
internal sealed class PointsAwarder
{
    private readonly PointsWriter _writer;

    internal PointsAwarder(PointsWriter writer) => _writer = writer;

    internal async Task AwardAsync(CancellationToken cancellationToken) =>
        await _writer.WriteAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>Hop two of the transitive chain: the save the handler must not reach.</summary>
internal sealed class PointsWriter
{
    private readonly IUnitOfWork _unitOfWork;

    internal PointsWriter(IUnitOfWork unitOfWork) => _unitOfWork = unitOfWork;

    internal async Task WriteAsync(CancellationToken cancellationToken) =>
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>The collaborator is an abstraction, so IL records only the interface call.</summary>
internal interface IBadgeGranter
{
    Task GrantAsync(CancellationToken cancellationToken);
}

/// <summary>The implementation behind <see cref="IBadgeGranter"/>, which saves.</summary>
internal sealed class BadgeGranter : IBadgeGranter
{
    private readonly IUnitOfWork _unitOfWork;

    internal BadgeGranter(IUnitOfWork unitOfWork) => _unitOfWork = unitOfWork;

    public async Task GrantAsync(CancellationToken cancellationToken) =>
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>Reaches the save only through an interface call, which the walk must resolve.</summary>
internal sealed class InterfaceDispatchSavingHandler : IDomainEventHandler<FixtureDomainEvent>
{
    private readonly IBadgeGranter _granter;

    internal InterfaceDispatchSavingHandler(IBadgeGranter granter) => _granter = granter;

    public Task HandleAsync(FixtureDomainEvent domainEvent, CancellationToken cancellationToken = default) =>
        _granter.GrantAsync(cancellationToken);
}

/// <summary>
/// Mutates in-memory state and returns. This is what a conforming handler looks like, and the rule
/// must stay silent about it.
/// </summary>
internal sealed class InnocentHandler : IDomainEventHandler<FixtureDomainEvent>
{
    private readonly List<string> _seen = [];

    public Task HandleAsync(FixtureDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        _seen.Add(domainEvent.GetType().Name);
        return Task.CompletedTask;
    }
}
