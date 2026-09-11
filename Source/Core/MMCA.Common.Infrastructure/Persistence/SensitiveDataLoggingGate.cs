using Microsoft.Extensions.Hosting;

namespace MMCA.Common.Infrastructure.Persistence;

/// <summary>
/// Decides whether EF Core may render parameter VALUES into its logs and exception messages
/// (<c>DbContextOptionsBuilder.EnableSensitiveDataLogging</c>).
/// </summary>
/// <remarks>
/// <para>
/// The answer is an AND, never a single switch. Sensitive-data logging is the single most useful
/// local debugging aid EF has and the single worst thing to leave on in a deployed environment: it
/// puts email addresses, tokens, password hashes and payment identifiers into whatever sink the
/// host's logging is wired to. Configuration travels (a copied appsettings file, a promoted
/// container image, an environment variable set once and forgotten), so the request to enable it is
/// honored only where it can do no harm: a host that reports itself as Development.
/// </para>
/// <para>
/// A null <see cref="IHostEnvironment"/> counts as NOT Development. Design-time tooling and a
/// directly-constructed test context register no environment at all, and "unknown" must fail
/// closed, exactly as <c>SmtpTransportSecurity</c> treats an unknown environment as production.
/// </para>
/// </remarks>
public static class SensitiveDataLoggingGate
{
    /// <summary>
    /// Resolves whether sensitive-data logging is permitted.
    /// </summary>
    /// <param name="settings">The bound persistence settings, or <see langword="null"/>.</param>
    /// <param name="environment">The host environment, or <see langword="null"/> when none is registered.</param>
    /// <returns>
    /// <see langword="true"/> only when the host asked for it AND reports the Development
    /// environment.
    /// </returns>
    public static bool IsEnabled(PersistenceSettings? settings, IHostEnvironment? environment)
        => settings?.EnableSensitiveDataLogging == true && environment?.IsDevelopment() == true;
}
