namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Fitness functions guarding pinned dependency versions that carry a known operational hazard
/// if bumped. Unlike the NetArchTest suites (which inspect compiled assemblies), these parse
/// <c>Directory.Packages.props</c> directly, so a Central Package Management bump is caught at
/// build time here rather than at runtime in a deployed broker host where CI never exercises it.
/// </summary>
public sealed class DependencyVersionTests
{
    [Theory]
    [InlineData("MassTransit")]
    [InlineData("MassTransit.RabbitMQ")]
    [InlineData("MassTransit.Azure.ServiceBus.Core")]
    public void MassTransit_MustNotExceedMajorVersion8(string packageId)
    {
        var version = ReadPinnedVersion(packageId);

        version.Should().NotBeNull(
            because: $"{packageId} must remain pinned in Directory.Packages.props");

        version!.Major.Should().BeLessThan(
            9,
            because: "MassTransit v9 requires a commercial license (MT_LICENSE); without it every "
                + "broker-enabled host fails the startup license check and crashes. A blanket "
                + "package update reintroduced v9 once before, and CI never starts a broker so the "
                + "build stayed green. Configure a license before bumping past v8 — see the comment "
                + "next to the MassTransit pins in Directory.Packages.props.");
    }

    private static Version? ReadPinnedVersion(string packageId)
    {
        var propsPath = FindUpwards("Directory.Packages.props");
        var raw = XDocument.Load(propsPath)
            .Descendants("PackageVersion")
            .FirstOrDefault(e => string.Equals(
                (string?)e.Attribute("Include"), packageId, StringComparison.Ordinal))
            ?.Attribute("Version")?.Value;

        return raw is null ? null : Version.Parse(raw);
    }

    private static string FindUpwards(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not locate {fileName} by walking up from {AppContext.BaseDirectory}.");
    }
}
