namespace MMCA.Common.Testing.Aspire.Preconditions;

/// <summary>
/// Turns a fixture's declared <see cref="AppHostEnvironmentRequirement"/> set into either "go" or a
/// sentence explaining what is missing. The sentence becomes the xUnit skip reason for the whole
/// collection, so a run on a machine without the preconditions reports a named skip rather than a
/// boot that fails somewhere deep in the orchestrator.
/// </summary>
public static class AppHostEnvironmentGate
{
    /// <summary>
    /// The opt-in variable. Set it to <c>1</c> or <c>true</c> to allow AppHost-backed collections to
    /// run. Absent by default so a developer's ordinary unit loop never starts an orchestrator, which
    /// is the slowest thing in any repo per assertion and can wedge a headless shell.
    /// </summary>
    public const string OptInVariable = "MMCA_APPHOST_TESTS";

    /// <summary>
    /// Evaluates the requirements against this machine.
    /// </summary>
    /// <param name="requirements">What the fixture needs.</param>
    /// <returns><see langword="null"/> to run, or the reason the collection must be skipped.</returns>
    public static string? Evaluate(AppHostEnvironmentRequirement requirements) => Evaluate(
        requirements,
        Environment.GetEnvironmentVariable,
        DockerAvailability.IsAvailable,
        DeveloperCertificateAvailability.IsPresent);

    /// <summary>
    /// Testable core of <see cref="Evaluate(AppHostEnvironmentRequirement)"/>: every machine fact
    /// arrives as a delegate.
    /// </summary>
    /// <param name="requirements">What the fixture needs.</param>
    /// <param name="readVariable">Reads an environment variable.</param>
    /// <param name="probeDocker">Answers whether a container runtime is reachable.</param>
    /// <param name="probeDeveloperCertificate">Answers whether the HTTPS development certificate is present.</param>
    /// <returns><see langword="null"/> to run, or the reason the collection must be skipped.</returns>
    internal static string? Evaluate(
        AppHostEnvironmentRequirement requirements,
        Func<string, string?> readVariable,
        Func<bool> probeDocker,
        Func<bool> probeDeveloperCertificate)
    {
        ArgumentNullException.ThrowIfNull(readVariable);
        ArgumentNullException.ThrowIfNull(probeDocker);
        ArgumentNullException.ThrowIfNull(probeDeveloperCertificate);

        if (requirements.HasFlag(AppHostEnvironmentRequirement.OptIn) && !IsOptedIn(readVariable(OptInVariable)))
        {
            return $"AppHost tests are opt-in: set {OptInVariable}=1 to boot a real AppHost. They start an orchestrator, so they are off by default.";
        }

        if (requirements.HasFlag(AppHostEnvironmentRequirement.Docker) && !probeDocker())
        {
            return $"No container runtime is reachable ({DockerAvailability.DockerHostVariable} is unset and the platform default endpoint is absent), and this AppHost declares container resources.";
        }

        if (requirements.HasFlag(AppHostEnvironmentRequirement.DeveloperCertificate) && !probeDeveloperCertificate())
        {
            return "The ASP.NET Core HTTPS development certificate is absent, so every https health probe would fail with UntrustedRoot. Run 'dotnet dev-certs https --trust' first; this fixture never installs one.";
        }

        return null;
    }

    /// <summary>Whether the opt-in variable carries an affirmative value.</summary>
    /// <param name="value">The raw variable value.</param>
    /// <returns><see langword="true"/> for the literal 1, or for the word "true" in any casing.</returns>
    private static bool IsOptedIn(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
