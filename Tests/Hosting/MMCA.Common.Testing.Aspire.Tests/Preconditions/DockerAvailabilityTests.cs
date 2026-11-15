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

    [Fact]
    public void WindowsEnginePipeProbe_AnswersFromThePipeFilesystem_AndNeverThrows()
    {
        // The public probe only reaches the pipe enumeration on Windows, so on the Linux CI runner it is
        // invoked directly: there the pipe root does not exist, and the IO failure must read as "no
        // runtime" rather than escape and fail the collection gate.
        var probe = typeof(DockerAvailability).GetMethod(
            "WindowsEnginePipeExists",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        probe.Should().NotBeNull("the pipe probe is the delegate IsAvailable() hands the Windows branch");

        var exists = (bool)probe!.Invoke(null, null)!;

        if (OperatingSystem.IsWindows())
        {
            var listed = new DirectoryInfo(@"\\.\pipe\")
                .GetFiles()
                .Any(file => string.Equals(file.Name, DockerAvailability.WindowsEnginePipeName, StringComparison.OrdinalIgnoreCase));
            exists.Should().Be(listed, "the probe reports exactly whether the engine pipe is listed");
        }
        else
        {
            exists.Should().BeFalse("there is no named-pipe filesystem off Windows");
        }
    }
}
