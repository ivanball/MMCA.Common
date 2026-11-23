using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Governance;

/// <summary>
/// The MassTransit ban (ADR-118), driven by the shared <see cref="DependencyVersionTestsBase"/>
/// (parses this repo's Directory.Packages.props), plus the rule it runs, pinned against
/// throwaway props files so the ban is known to bite and not to pass vacuously.
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

    private static string WriteProps(params (string Id, string Version)[] packages)
    {
        var document = new XDocument(
            new XElement("Project",
                new XElement("ItemGroup",
                    packages.Select(p => new XElement("PackageVersion",
                        new XAttribute("Include", p.Id),
                        new XAttribute("Version", p.Version))))));

        var path = Path.Combine(Path.GetTempPath(), $"mmca-packages-{Guid.NewGuid():N}.props");
        document.Save(path);
        return path;
    }
}
