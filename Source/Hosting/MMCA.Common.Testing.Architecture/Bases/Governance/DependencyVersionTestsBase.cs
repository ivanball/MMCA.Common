namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Dependency fitness function for the messaging-library policy (ADR-118): the broker transports are
/// framework code over the vendor SDKs, and MassTransit is banned outright. Parses the repo's
/// <c>Directory.Packages.props</c>, so a <c>MassTransit*</c> pin fails the build before any project
/// references it (CI never starts a broker, so a reintroduced library would otherwise stay green).
/// Consumers may subclass this base as-is: the ban holds for every repo.
/// </summary>
public abstract class DependencyVersionTestsBase
{
    [Fact]
    public void MassTransit_MustNotBeReferenced() =>
        ArchitectureRules.NoPackageVersionWithPrefix(
            "MassTransit",
            reason: "MassTransit is banned (ADR-118, Amendment 2026-11-23): the broker transports are "
                + "framework code over the vendor SDKs behind IMessageBus, and v9 of the library needs a "
                + "commercial license. Remove every MassTransit* PackageVersion.");
}
