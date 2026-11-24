using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Governance;

/// <summary>
/// The MassTransit ban (ADR-118), driven by the shared <see cref="DependencyVersionTestsBase"/>
/// (parses this repo's Directory.Packages.props), plus the rule it runs and the shipped
/// version-ceiling rule consumers still call, pinned against throwaway props files so each is known
/// to bite and not to pass vacuously.
/// </summary>
public sealed class DependencyVersionTests : DependencyVersionTestsBase
{
    [Theory]
    [InlineData("MassTransit")]
    [InlineData("MassTransit.RabbitMQ")]
    [InlineData("masstransit.azure.servicebus.core")]
    public void NoPackageVersionWithPrefix_WhenABannedPackageIsPinned_Fails(string packageId)
    {
        var propsPath = WriteProps(("RabbitMQ.Client", "7.2.2"), (packageId, "8.5.12"));
        try
        {
            var act = () => ArchitectureRules.NoPackageVersionWithPrefix(propsPath, "MassTransit", "banned");

            act.Should().Throw<Exception>().WithMessage($"*{packageId}*");
        }
        finally
        {
            File.Delete(propsPath);
        }
    }

    [Fact]
    public void NoPackageVersionWithPrefix_WhenNoBannedPackageIsPinned_Passes()
    {
        var propsPath = WriteProps(("RabbitMQ.Client", "7.2.2"), ("Azure.Messaging.ServiceBus", "7.21.0"));
        try
        {
            var act = () => ArchitectureRules.NoPackageVersionWithPrefix(propsPath, "MassTransit", "banned");

            act.Should().NotThrow();
        }
        finally
        {
            File.Delete(propsPath);
        }
    }

    [Fact]
    public void PinnedPackageMajorBelow_WhenPinnedBelowTheCeiling_Passes()
    {
        var propsPath = WriteProps(("Azure.Messaging.ServiceBus", "7.21.0"), ("RabbitMQ.Client", "7.2.2"));
        try
        {
            var act = () => ArchitectureRules.PinnedPackageMajorBelow(propsPath, "RabbitMQ.Client", 8, "ceiling");

            act.Should().NotThrow();
        }
        finally
        {
            File.Delete(propsPath);
        }
    }

    [Theory]
    [InlineData("8.0.0")]
    [InlineData("8.0")]
    [InlineData("9.1.3")]
    public void PinnedPackageMajorBelow_WhenPinnedAtOrAboveTheCeiling_Fails(string version)
    {
        var propsPath = WriteProps(("RabbitMQ.Client", version));
        try
        {
            var act = () => ArchitectureRules.PinnedPackageMajorBelow(propsPath, "RabbitMQ.Client", 8, "the 8.x client is not supported yet");

            act.Should().Throw<Exception>().WithMessage("*the 8.x client is not supported yet*");
        }
        finally
        {
            File.Delete(propsPath);
        }
    }

    [Fact]
    public void PinnedPackageMajorBelow_WhenThePackageIsNotPinned_FailsNamingIt()
    {
        var propsPath = WriteProps(("Azure.Messaging.ServiceBus", "7.21.0"));
        try
        {
            var act = () => ArchitectureRules.PinnedPackageMajorBelow(propsPath, "RabbitMQ.Client", 8, "ceiling");

            act.Should().Throw<Exception>().WithMessage("*RabbitMQ.Client must remain pinned*");
        }
        finally
        {
            File.Delete(propsPath);
        }
    }

    [Fact]
    public void PinnedPackageMajorBelow_WhenTheEntryHasNoVersionAttribute_FailsAsNotPinned()
    {
        var propsPath = WriteProps(("RabbitMQ.Client", null));
        try
        {
            var act = () => ArchitectureRules.PinnedPackageMajorBelow(propsPath, "RabbitMQ.Client", 8, "ceiling");

            act.Should().Throw<Exception>().WithMessage("*RabbitMQ.Client must remain pinned*");
        }
        finally
        {
            File.Delete(propsPath);
        }
    }

    [Fact]
    public void PinnedPackageMajorBelow_AgainstThisRepo_FindsTheCentralPackageFile()
    {
        // The overload without a path walks up from the test output folder to this repo's
        // Directory.Packages.props, where the RabbitMQ client is pinned below major 100.
        var act = () => ArchitectureRules.PinnedPackageMajorBelow("RabbitMQ.Client", 100, "ceiling");

        act.Should().NotThrow();
    }

    private static string WriteProps(params (string Id, string? Version)[] packages)
    {
        var document = new XDocument(
            new XElement("Project",
                new XElement("ItemGroup",
                    packages.Select(p => new XElement("PackageVersion",
                        new XAttribute("Include", p.Id),
                        p.Version is null ? null : new XAttribute("Version", p.Version))))));

        var path = Path.Combine(Path.GetTempPath(), $"mmca-packages-{Guid.NewGuid():N}.props");
        document.Save(path);
        return path;
    }
}
