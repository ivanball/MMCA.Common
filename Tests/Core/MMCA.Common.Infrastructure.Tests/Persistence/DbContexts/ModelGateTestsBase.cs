using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DbContexts;

/// <summary>
/// The contract every settings-gated table mapped by <see cref="Common.Infrastructure.Persistence.DbContexts.ApplicationDbContext"/>
/// shares on a single-database host: a host that never registered the capability's settings, or
/// registered them without enabling it, builds the model it had before the table shipped, and an
/// enabled host gets the table on the default source. Each subclass supplies the gated entity, a way to
/// build a real context in each of the three states, and the mapping checks specific to its table.
/// </summary>
/// <remarks>
/// Every case asks for its context with its OWN marker type (the <c>Case*</c> classes below). EF
/// caches a built model per (context type, physical source name), process-wide, so a subclass whose
/// context type is closed over the marker gets one cache entry per case and stays order-independent.
/// A subclass that turns service-provider caching off instead may ignore the marker.
/// </remarks>
public abstract class ModelGateTestsBase
{
    /// <summary>The opt-in state a case builds its context in.</summary>
    protected enum GateState
    {
        /// <summary>The capability's settings (or registration) were never added to the container.</summary>
        NeverRegistered,

        /// <summary>The settings are registered with their defaults, so the capability is off.</summary>
        Disabled,

        /// <summary>The capability is on and targets the default source.</summary>
        Enabled,
    }

    /// <summary>The entity whose table the gate adds or leaves out.</summary>
    protected abstract Type GatedEntityType { get; }

    /// <summary>
    /// Builds a real context over the shared base for the default source in <paramref name="state"/>.
    /// Nothing connects: the assertions read <c>context.Model</c>.
    /// </summary>
    /// <typeparam name="TCase">Marker that gives each case its own EF model cache entry.</typeparam>
    protected abstract DbContext CreateGateContext<TCase>(GateState state);

    /// <summary>The table-specific mapping checks an enabled single-database host must pass.</summary>
    protected abstract void AssertEnabledMapping(IEntityType entity);

    [Fact]
    public void Disabled_NoContextGetsTheTable()
    {
        using var context = CreateGateContext<CaseDisabled>(GateState.Disabled);

        context.Model.FindEntityType(GatedEntityType).Should().BeNull(
            "registering the settings with their defaults is not opting in, so the host must not gain a table in its next migration");
    }

    [Fact]
    public void NeverRegistered_NoContextGetsTheTable()
    {
        using var context = CreateGateContext<CaseNeverRegistered>(GateState.NeverRegistered);

        context.Model.FindEntityType(GatedEntityType).Should().BeNull(
            "a host that never registers the capability must build the model it had before, and that absence reads as disabled rather than failing context construction");
    }

    [Fact]
    public void EnabledOnASingleHost_TheDefaultSourceGetsTheTable()
    {
        using var context = CreateGateContext<CaseEnabledOnASingleHost>(GateState.Enabled);

        var entity = context.Model.FindEntityType(GatedEntityType);

        entity.Should().NotBeNull();
        AssertEnabledMapping(entity!);
    }

    private sealed class CaseNeverRegistered;

    private sealed class CaseDisabled;

    private sealed class CaseEnabledOnASingleHost;
}
