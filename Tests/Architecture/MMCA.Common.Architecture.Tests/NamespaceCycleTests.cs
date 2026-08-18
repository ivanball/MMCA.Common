using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Namespace acyclicity for the MMCA.Common framework packages, driven by the shared rule library
/// (<see cref="NamespaceCycleTestsBase"/>) over <see cref="CommonArchitectureMap"/>.
/// </summary>
public sealed class NamespaceCycleTests : NamespaceCycleTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    /// <summary>
    /// The one accepted tangle in the framework today, inside <c>MMCA.Common.Infrastructure</c>:
    /// <c>root -&gt; Settings -&gt; Persistence -&gt; root</c>. Each edge is deliberate and none of the
    /// three namespaces is extractable on its own anyway (they are one assembly, one package):
    /// <list type="number">
    /// <item><description>
    /// <c>root -&gt; Settings</c>: <c>DependencyInjection</c> lives in the root namespace and binds every
    /// settings class, which is the point of a composition root.
    /// </description></item>
    /// <item><description>
    /// <c>Settings -&gt; Persistence</c>: <c>TenancySettingsValidator</c> takes an optional
    /// <c>IDataSourceResolver</c> so a tenant override that names a non-existent physical source fails the
    /// boot rather than silently resolving cross-tenant at runtime (validation must see the resolved
    /// sources; a settings class that cannot check itself against reality is worth less than the edge).
    /// </description></item>
    /// <item><description>
    /// <c>Persistence -&gt; root</c>: the <c>EntityTypeConfiguration*</c> shims carry
    /// <c>[UseDataSource]</c>/<c>[UseDatabase]</c>, marker attributes that live in the root namespace
    /// precisely BECAUSE consumers annotate their own configurations with them. Pushing them down into
    /// <c>Persistence</c> would make the public annotation surface deeper for every consumer to fix an
    /// internal graph edge.
    /// </description></item>
    /// </list>
    /// The allowance covers the whole strongly connected component, so a fourth namespace joining this
    /// tangle still fails the test.
    /// </summary>
    protected override IReadOnlyList<string> AllowedCycleNamespaces =>
    [
        "MMCA.Common.Infrastructure",
        "MMCA.Common.Infrastructure.Persistence",
        "MMCA.Common.Infrastructure.Settings",
    ];
}
