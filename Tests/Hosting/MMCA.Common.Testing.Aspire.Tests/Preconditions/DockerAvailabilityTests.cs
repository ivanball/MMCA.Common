using AwesomeAssertions;
using MMCA.Common.Testing.Aspire.Preconditions;

namespace MMCA.Common.Testing.Aspire.Tests.Preconditions;

/// <summary>
/// Both platform branches of the runtime probe, proven on either operating system because every
/// platform fact is injected.
/// </summary>
public sealed class DockerAvailabilityTests
{
    [Fact]
    public void ConfiguredDaemonAddress_WinsOnEveryPlatform() =>
        DockerAvailability
            .IsAvailable(_ => "tcp://remote:2375", isWindows: true, _ => false, () => false)
            .Should().BeTrue("an explicit DOCKER_HOST is the answer regardless of local endpoints");

    [Fact]
    public void BlankDaemonAddress_FallsThroughToTheDefaultEndpoint() =>
        DockerAvailability
            .IsAvailable(_ => "   ", isWindows: false, path => path == DockerAvailability.UnixSocketPath, () => false)
            .Should().BeTrue();

    [Fact]
    public void Linux_ProbesTheUnixSocket() =>
        DockerAvailability
            .IsAvailable(_ => null, isWindows: false, path => path == DockerAvailability.UnixSocketPath, () => true)
            .Should().BeTrue();

    [Fact]
    public void Linux_ReportsNoRuntime_WhenTheSocketIsAbsent() =>
        DockerAvailability
            .IsAvailable(_ => null, isWindows: false, _ => false, () => true)
            .Should().BeFalse("the Windows pipe probe must not be consulted on Linux");

    [Fact]
    public void Windows_ProbesTheEnginePipe() =>
        DockerAvailability
            .IsAvailable(_ => null, isWindows: true, _ => true, () => true)
            .Should().BeTrue();

    [Fact]
    public void Windows_ReportsNoRuntime_WhenThePipeIsAbsent() =>
        DockerAvailability
            .IsAvailable(_ => null, isWindows: true, _ => true, () => false)
            .Should().BeFalse("the Unix socket probe must not be consulted on Windows");

    [Fact]
    public void PublicProbe_AnswersWithoutThrowing()
    {
        // The real probe touches the filesystem, so the only portable claim is that it answers rather
        // than throwing on a machine in any state.
        var act = () => DockerAvailability.IsAvailable();

        act.Should().NotThrow();
    }
}
