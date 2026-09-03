using System.Reflection;
using MMCA.Common.Infrastructure.Persistence.DataSources;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Design;

/// <summary>
/// Options for <see cref="DesignTimeDbContextHelper"/>. A downstream migrations project supplies
/// its module's configuration assemblies and connection settings; the data source name normally
/// comes from the <c>dotnet ef ... -- --datasource &lt;Name&gt;</c> argument.
/// </summary>
public sealed class DesignTimeDbContextOptions
{
    /// <summary>
    /// Gets or sets the logical data source name to build the context for. When
    /// <see langword="null"/>, the name is parsed from the <c>--datasource</c> design-time
    /// argument, falling back to <c>Default</c>.
    /// </summary>
    public string? DataSourceName { get; set; }

    /// <summary>
    /// Gets or sets the top-level connection strings (the <c>Default</c> source), including
    /// <c>SQLServerMigrationsAssembly</c>.
    /// </summary>
    public ConnectionStringSettings ConnectionStrings { get; set; } = new();

    /// <summary>Gets the named data source entries (mirrors the <c>DataSources</c> configuration section).</summary>
    public Dictionary<string, DataSourceEntrySettings> DataSources { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the recurring job table (<c>ScheduledJobs</c>) is part
    /// of the design-time model, mirroring <c>Scheduler:Enabled</c> at runtime. Defaults to
    /// <see langword="false"/>, so <c>dotnet ef</c> keeps producing exactly the migrations it
    /// produced before the scheduler shipped.
    /// </summary>
    /// <remarks>
    /// Set it to <see langword="true"/> in the migrations project for the <c>Default</c> data source
    /// of a host that calls <c>AddScheduledJobs</c>, and only there: the table is host-scoped, so a
    /// second migrations project that also enabled it would create a second copy. The flag must
    /// match the host's configuration, or the scaffolded migrations and the running model disagree.
    /// </remarks>
    public bool EnableScheduler { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the change-history table (<c>AuditTrailEntries</c>) is
    /// part of the design-time model, mirroring <c>AuditTrail:Enabled</c> at runtime. Defaults to
    /// <see langword="false"/>, so <c>dotnet ef</c> keeps producing exactly the migrations it
    /// produced before the audit trail shipped.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="EnableScheduler"/>, set it in the migrations project of <b>every</b> data
    /// source whose entities are audited: a trail row is written to the database holding the entity
    /// that changed, so each of those databases needs the table. The flag must match the host's
    /// configuration, or the scaffolded migrations and the running model disagree.
    /// </remarks>
    public bool EnableAuditTrail { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the refresh-session table (<c>RefreshSessions</c>) is
    /// part of the design-time model, mirroring <c>RefreshSessions:Enabled</c> at runtime. Defaults
    /// to <see langword="false"/>, so <c>dotnet ef</c> keeps producing exactly the migrations it
    /// produced before multi-device sessions shipped.
    /// </summary>
    /// <remarks>
    /// Set it in the migrations project of the <b>Identity</b> database only, like
    /// <see cref="EnableScheduler"/> and unlike <see cref="EnableAuditTrail"/>: sessions are one
    /// module's data, so a second migrations project that also enabled it would scaffold a second
    /// copy of the table in another database. There is no data-source setting to match here: the
    /// helper registers the source this context actually resolved to, so the flag opens the gate for
    /// exactly the context <c>--datasource</c> selected. The flag must match the host's
    /// <c>RefreshSessions:Enabled</c>, or the scaffolded migrations and the running model disagree
    /// (which is what <c>has-pending-model-changes</c> reports).
    /// </remarks>
    public bool EnableRefreshSessions { get; set; }

    /// <summary>
    /// Gets the assemblies containing the entity type configurations to include in the model.
    /// Must be listed explicitly — the AppDomain scan used at runtime sees nothing at design time.
    /// </summary>
    public IList<Assembly> ConfigurationAssemblies { get; } = [];

    /// <summary>Adds an assembly containing entity type configurations.</summary>
    /// <param name="assembly">The configuration assembly.</param>
    /// <returns>These options, for chaining.</returns>
    public DesignTimeDbContextOptions AddConfigurationAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (!ConfigurationAssemblies.Contains(assembly))
        {
            ConfigurationAssemblies.Add(assembly);
        }

        return this;
    }
}
