namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Fitness function: Domain and Application code takes time as an input and never reads the ambient
/// clock (<c>DateTime.UtcNow</c>, <c>DateTime.Now</c>, <c>DateTimeOffset.UtcNow</c>,
/// <c>DateTimeOffset.Now</c>). A handler injects <see cref="TimeProvider"/> and passes the instant into
/// the domain method, so a test can drive every expiry, cutoff and overdue branch without sleeping.
/// The framework's <c>BaseDomainEvent</c> occurrence stamp is always exempt.
/// <para>
/// Adoption in a repo with existing clock reads: subclass, run once, and either thread the instant
/// through or move the reported type or member into <see cref="AllowedClockReaders"/> with a comment
/// saying why reading the clock there is right (a one-shot backfill, for example).
/// </para>
/// </summary>
public abstract class ClockReadTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Type full names (<c>MMCA.X.Sales.Domain.Orders.Order</c>), namespace prefixes, or single
    /// members (<c>MMCA.X.Sales.Domain.Orders.Order.RepublishFulfillment</c>) where reading the clock
    /// is the deliberate, reviewed choice. Empty by default.
    /// </summary>
    protected virtual IReadOnlyCollection<string> AllowedClockReaders => [];

    [Fact]
    public void DomainAndApplication_ShouldNotReadTheAmbientClock() =>
        ArchitectureRules.DomainAndApplicationDoNotReadTheClock(Map, AllowedClockReaders);
}
