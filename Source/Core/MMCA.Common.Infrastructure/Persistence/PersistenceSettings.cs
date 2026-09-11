using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Infrastructure.Persistence;

/// <summary>
/// Configuration for relational persistence behavior, bound from the <c>Persistence</c> section.
/// Every property defaults to the value the framework applied implicitly before the section
/// existed, so the section is optional in <c>appsettings.json</c>.
/// </summary>
public sealed class PersistenceSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "Persistence";

    /// <summary>
    /// Gets the command timeout, in seconds, applied to every SQL command the SQL Server context
    /// issues. The default of <c>30</c> matches the previous implicit ADO.NET default, so an app
    /// that sets nothing sees no behavior change; raise it for reporting-style workloads whose
    /// queries legitimately run longer than half a minute.
    /// </summary>
    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Gets a value indicating whether EF Core may include parameter VALUES in its logs and
    /// exception messages (<c>EnableSensitiveDataLogging</c>). Defaults to <see langword="false"/>,
    /// which is the framework's behavior before the flag existed.
    /// <para>
    /// <b>And-gated on the environment.</b> Setting this to <see langword="true"/> is a request, not
    /// a switch: the framework honors it only when <c>IHostEnvironment.IsDevelopment()</c> is also
    /// true, so a value that leaks into a Staging or Production configuration file, a container
    /// image or an environment variable cannot put customer data, tokens or password hashes into a
    /// log sink. A host with no <c>IHostEnvironment</c> registered at all (design time, a
    /// directly-constructed test context) counts as not Development.
    /// </para>
    /// <para>
    /// <b>Pair it with a log level.</b> The flag decides whether parameter values are rendered; it
    /// does not make EF log the statements in the first place. To see the SQL locally, set
    /// <c>Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command</c> to <c>Information</c>
    /// in <c>appsettings.Development.json</c> (that file only, never the base
    /// <c>appsettings.json</c>).
    /// </para>
    /// </summary>
    public bool EnableSensitiveDataLogging { get; init; }
}
