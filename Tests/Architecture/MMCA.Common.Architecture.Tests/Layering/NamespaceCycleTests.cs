using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Layering;

/// <summary>
/// Namespace acyclicity for the MMCA.Common framework packages, driven by the shared rule library
/// (<see cref="NamespaceCycleTestsBase"/>) over <see cref="CommonArchitectureMap"/>.
/// </summary>
public sealed class NamespaceCycleTests : NamespaceCycleTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    /// <summary>
    /// The one accepted tangle in the framework today, inside <c>MMCA.Common.Infrastructure</c>:
    /// <c>root -&gt; Messaging -&gt; Persistence -&gt; root</c>. Each edge is deliberate and none of the
    /// three namespaces is extractable on its own anyway (they are one assembly, one package):
    /// <list type="number">
    /// <item><description>
    /// <c>root -&gt; Messaging</c>: <c>DependencyInjection</c> lives in the root namespace and binds the
    /// buses and their settings, which is the point of a composition root.
    /// </description></item>
    /// <item><description>
    /// <c>Messaging -&gt; Persistence</c>: the buses are the outbox transport. <c>InProcessEventBus</c> and
    /// <c>BrokerEventBus</c> enqueue <c>OutboxMessage</c> rows and wake the <c>OutboxProcessor</c> through
    /// <c>IOutboxSignal</c>, and the consumers resolve the physical source through <c>IDataSourceResolver</c>;
    /// the outbox in turn reports through <c>BrokerMetrics</c> and reads <c>MessageBusSettings</c>. Splitting
    /// the two would put half of one delivery guarantee on each side of a package boundary.
    /// </description></item>
    /// <item><description>
    /// <c>Persistence -&gt; root</c>: the <c>EntityTypeConfiguration*</c> shims carry
    /// <c>[UseDataSource]</c>/<c>[UseDatabase]</c>, marker attributes that live in the root namespace
    /// precisely BECAUSE consumers annotate their own configurations with them. Pushing them down into
    /// <c>Persistence</c> would make the public annotation surface deeper for every consumer to fix an
    /// internal graph edge.
    /// </description></item>
    /// </list>
    /// <c>Scheduling</c> joined the component deliberately in the 2026-09 code-health wave, and both of
    /// its edges are load-bearing: <c>Scheduling -&gt; Persistence</c> because <c>ScheduledJobRunner</c>
    /// leases and records its runs in the database, and <c>Persistence -&gt; Scheduling</c> because the
    /// persistence sweeps (outbox, internal-command and refresh-session cleanup, the permission-grant
    /// refresh) now run on the shared <c>PeriodicBackgroundService</c> loop instead of four hand-written
    /// copies of it. Moving that public base type out of <c>Scheduling</c> would be a namespace rename
    /// for every consumer subclass to buy an internal graph edge.
    /// The allowance covers the whole strongly connected component, so a fifth namespace joining this
    /// tangle still fails the test. (Before the 2026-09 feature-by-folder reorganization the third node
    /// was <c>Settings</c>; that folder dissolved into the features it configured, and the tenancy
    /// validator that carried the <c>Settings -&gt; Persistence</c> edge now lives in <c>Persistence/Tenancy</c>.)
    /// </summary>
    protected override IReadOnlyList<string> AllowedCycleNamespaces =>
    [
        "MMCA.Common.Infrastructure",
        "MMCA.Common.Infrastructure.Persistence",
        "MMCA.Common.Infrastructure.Messaging",
        "MMCA.Common.Infrastructure.Scheduling",
    ];
}
