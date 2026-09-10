namespace MMCA.Common.Testing.Aspire.Preconditions;

/// <summary>
/// Cheap detection of a reachable container runtime, used by <see cref="AppHostEnvironmentGate"/> to
/// decide whether an AppHost that declares container resources can start at all.
/// <para>
/// Deliberately a socket/pipe probe rather than shelling out to <c>docker info</c>: the gate runs
/// once per collection before anything else, and a process launch that can block for seconds on a
/// misconfigured daemon is the wrong thing to put in front of a skip decision. The probe answers the
/// only question the gate has: is there an endpoint a runtime would be listening on.
/// </para>
/// </summary>
public static class DockerAvailability
{
    /// <summary>The environment variable an explicit daemon address is passed in. Set, it wins.</summary>
    public const string DockerHostVariable = "DOCKER_HOST";

    /// <summary>The default Unix domain socket a Docker or Podman daemon listens on.</summary>
    public const string UnixSocketPath = "/var/run/docker.sock";

    /// <summary>The named pipe Docker Desktop listens on, without the pipe directory prefix.</summary>
    public const string WindowsEnginePipeName = "docker_engine";

    /// <summary>The Windows named-pipe filesystem root the engine pipe is enumerated from.</summary>
    internal const string WindowsPipeDirectory = @"\\.\pipe\";

    /// <summary>
    /// Whether a container runtime looks reachable from this process.
    /// </summary>
    /// <returns><see langword="true"/> when a daemon address is configured or the platform's default endpoint exists.</returns>
    public static bool IsAvailable() => IsAvailable(
        Environment.GetEnvironmentVariable,
        OperatingSystem.IsWindows(),
        File.Exists,
        WindowsEnginePipeExists);

    /// <summary>
    /// Testable core of <see cref="IsAvailable()"/>: every platform fact arrives as a delegate, so
    /// both branches can be proven on either operating system.
    /// </summary>
    /// <param name="readVariable">Reads an environment variable.</param>
    /// <param name="isWindows">Whether the host platform is Windows.</param>
    /// <param name="fileExists">Tests for the Unix socket.</param>
    /// <param name="windowsEnginePipeExists">Tests for the Windows engine pipe.</param>
    /// <returns><see langword="true"/> when a container runtime looks reachable.</returns>
    internal static bool IsAvailable(
        Func<string, string?> readVariable,
        bool isWindows,
        Func<string, bool> fileExists,
        Func<bool> windowsEnginePipeExists)
    {
        ArgumentNullException.ThrowIfNull(readVariable);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(windowsEnginePipeExists);

        if (!string.IsNullOrWhiteSpace(readVariable(DockerHostVariable)))
        {
            return true;
        }

        return isWindows ? windowsEnginePipeExists() : fileExists(UnixSocketPath);
    }

    /// <summary>
    /// Enumerates the named-pipe filesystem looking for the engine pipe. Any IO or access failure is
    /// answered with "no runtime": a gate that cannot see the pipe must skip the collection rather
    /// than fail it, and a hardened machine that refuses the enumeration is indistinguishable from
    /// one with no daemon as far as the AppHost is concerned.
    /// </summary>
    /// <returns><see langword="true"/> when the engine pipe exists.</returns>
    private static bool WindowsEnginePipeExists()
    {
        try
        {
            return Directory
                .EnumerateFiles(WindowsPipeDirectory)
                .Any(static path => path.EndsWith(WindowsEnginePipeName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
