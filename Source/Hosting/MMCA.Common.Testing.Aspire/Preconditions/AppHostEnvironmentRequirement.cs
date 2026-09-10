namespace MMCA.Common.Testing.Aspire.Preconditions;

/// <summary>
/// What an AppHost-backed test collection needs from the machine it runs on. A fixture declares its
/// requirements and <see cref="AppHostEnvironmentGate"/> turns the ones that are missing into a
/// human-readable skip reason, so an unmet precondition reads as "skipped, and here is why" instead
/// of a twelve-minute wait that ends in a timeout nobody can diagnose.
/// </summary>
[Flags]
public enum AppHostEnvironmentRequirement
{
    /// <summary>Nothing: the collection always runs. Only sensible for a project-only AppHost on a machine that is known good.</summary>
    None = 0,

    /// <summary>
    /// The opt-in variable (<see cref="AppHostEnvironmentGate.OptInVariable"/>) must be set. This is
    /// the default because booting a real AppHost starts an orchestrator, and a developer running the
    /// unit loop must never pay for that by accident.
    /// </summary>
    OptIn = 1,

    /// <summary>A container runtime must be reachable, because the AppHost declares container resources.</summary>
    Docker = 2,

    /// <summary>
    /// The ASP.NET Core HTTPS development certificate must be present, because at least one resource
    /// launches with an <c>https</c> profile and its health probe negotiates TLS against that
    /// certificate. Detected only; this package never installs or trusts a certificate.
    /// </summary>
    DeveloperCertificate = 4,
}
