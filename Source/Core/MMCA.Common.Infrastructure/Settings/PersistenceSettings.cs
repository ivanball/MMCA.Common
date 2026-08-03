using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Infrastructure.Settings;

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
}
