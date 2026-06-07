namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Named data sources bound from the <c>DataSources</c> configuration section, keyed by
/// <b>logical</b> source name (e.g. a module name like <c>"Conference"</c>). Enables
/// "database per microservice": each logical name maps to its own connection strings while
/// unmapped names fall back to the top-level <c>ConnectionStrings</c> (the <c>Default</c> source).
/// <para>
/// Built directly from configuration in <c>AddInfrastructure</c> (root-level dictionary sections
/// do not bind through the options pipeline) and registered as a singleton.
/// </para>
/// </summary>
public sealed class DataSourcesSettings
{
    /// <summary>Configuration section name.</summary>
    public static readonly string SectionName = "DataSources";

    /// <summary>
    /// Initializes the settings from the bound dictionary.
    /// </summary>
    /// <param name="sources">Named source entries keyed by logical name; may be empty.</param>
    /// <exception cref="InvalidOperationException">A reserved or empty logical name is used.</exception>
    public DataSourcesSettings(IReadOnlyDictionary<string, DataSourceEntrySettings>? sources = null)
    {
        Sources = sources ?? new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal);

        foreach (var name in Sources.Keys)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("DataSources entry names must be non-empty.");
            }

            if (string.Equals(name, Application.Interfaces.Infrastructure.DataSourceKey.DefaultName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The DataSources entry name \"{name}\" is reserved. The Default source is configured " +
                    "via the top-level ConnectionStrings section; remove the entry or rename it.");
            }
        }
    }

    /// <summary>Gets the named source entries keyed by logical name.</summary>
    public IReadOnlyDictionary<string, DataSourceEntrySettings> Sources { get; }
}
