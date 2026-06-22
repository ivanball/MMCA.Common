namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Dependency-pin fitness function for the MassTransit v9 commercial-license trap: v9 fails the startup
/// license check and crashes every broker-enabled host, and CI never starts a broker so a blanket bump
/// otherwise stays green. Parses <c>Directory.Packages.props</c> so the pin is caught at build time.
/// Repos that don't reference MassTransit can override <see cref="MassTransitPackageIds"/> with an empty
/// list (the pin is then vacuous). NOTE: the consumer repos (MMCA.ADC, MMCA.Store) do NOT pin MassTransit
/// (it flows transitively via <c>MMCA.Common.Infrastructure</c>), so they must NOT subclass this base with
/// the default list (the "must remain pinned" assertion would fail on a pin they do not declare). The v8
/// pin is enforced here, in MMCA.Common, where MassTransit is actually pinned; consumers inherit it
/// transitively. If a consumer ever needs its own guard, override <see cref="MassTransitPackageIds"/> to
/// assert the resolved/transitive version rather than a <c>Directory.Packages.props</c> entry it lacks.
/// </summary>
public abstract class DependencyVersionTestsBase
{
    protected virtual IReadOnlyList<string> MassTransitPackageIds =>
    [
        "MassTransit",
        "MassTransit.RabbitMQ",
        "MassTransit.Azure.ServiceBus.Core",
    ];

    [Fact]
    public void MassTransit_MustNotExceed_MajorVersion8()
    {
        foreach (var packageId in MassTransitPackageIds)
        {
            ArchitectureRules.PinnedPackageMajorBelow(
                packageId,
                exclusiveMajorCeiling: 9,
                reason: "MassTransit v9 requires a commercial license (MT_LICENSE); without it every "
                    + "broker-enabled host fails the startup license check and crashes. A blanket package "
                    + "update reintroduced v9 once before, and CI never starts a broker so the build stayed "
                    + "green. Configure a license before bumping past v8.");
        }
    }
}
