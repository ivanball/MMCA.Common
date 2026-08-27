namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Domain-event-handler purity fitness function: a handler must not persist. Dispatch happens after
/// <c>SaveChangesAsync</c> (and after commit inside a <c>ITransactional</c> command), so a handler
/// that saves opens a second write in the middle of the first one: it re-enters the change tracker,
/// can raise a fresh event cascade, and persists work the outer transaction may still roll back.
/// Handlers mutate state and let the owning unit of work flush it; an independent write belongs on
/// the outbox.
/// <para>
/// The rule is TRANSITIVE: it walks the call graph out of every handler method, so the common real
/// shape (handler to a domain service to <c>SaveChangesAsync</c>) is caught, not just a save typed
/// into the handler itself.
/// </para>
/// <para>
/// Adoption in a repo with an existing cascade: subclass, run once, and move each reported type into
/// <see cref="AllowedSavingTypes"/> with a comment recording why the save is accepted for now. An
/// entry both silences the type and stops the walk from descending into it, so the list stays a
/// reviewed inventory of the deliberate exceptions rather than a mute button.
/// </para>
/// </summary>
public abstract class DomainEventHandlerSaveTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Type full names (<c>MMCA.X.Engagement.Application.Points.PointsAwarder</c>) or namespace
    /// prefixes (<c>MMCA.X.Engagement.Application.Points</c>) that are neither reported nor walked
    /// into. Defaults to the framework itself: <c>MMCA.Common</c>'s outbox event bus persists by
    /// design, and a handler publishing an integration event is not the defect this rule hunts. A
    /// handler calling a save DIRECTLY is still reported, so the default silences nothing real.
    /// </summary>
    protected virtual IReadOnlyCollection<string> AllowedSavingTypes => ["MMCA.Common"];

    /// <summary>
    /// How many calls deep the walk goes out of a handler method. Six covers the realistic
    /// handler to service to helper to repository chain while keeping the scan fast; raise it in a
    /// repo with deeper delegation, at the cost of a longer run.
    /// </summary>
    protected virtual int MaxCallDepth => 6;

    [Fact]
    public void DomainEventHandlers_ShouldNotReach_SaveChanges() =>
        ArchitectureRules.DomainEventHandlersDoNotSave(Map, AllowedSavingTypes, MaxCallDepth);
}
