using System.Globalization;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;

namespace MMCA.Common.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Startup validation for <see cref="TenancySettings"/>, registered by <c>AddMultiTenancy</c> with
/// <c>ValidateOnStart</c>. Every failure here is a misconfiguration that would otherwise surface as
/// silent cross-tenant behavior at runtime, which is exactly the class of bug tenancy exists to
/// prevent, so it is worth a failed boot.
/// </summary>
/// <remarks>
/// The data-source checks need the resolved physical sources, so they run only when
/// <see cref="IDataSourceResolver"/> is registered (it is, whenever <c>AddInfrastructure</c> ran).
/// A container without it still gets the strategy and connection-string checks.
/// </remarks>
/// <param name="resolver">
/// The resolver used to confirm an override names a real physical source, or <see langword="null"/>
/// when persistence was not registered.
/// </param>
internal sealed class TenancySettingsValidator(IDataSourceResolver? resolver = null)
    : IValidateOptions<TenancySettings>
{
    /// <summary>The engines an override can carry a connection string for.</summary>
    private static readonly DataSource[] Engines =
        [DataSource.SQLServer, DataSource.Sqlite, DataSource.CosmosDB];

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TenancySettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        ValidateResolutionOrder(options, failures);

        foreach (var (tenantId, tenant) in options.Tenants)
        {
            ValidateTenant(tenantId, tenant, failures);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Rejects a resolution order naming a strategy the framework does not implement. The enum
    /// member exists so the configuration contract is stable, but selecting it must not read as
    /// "resolve nothing".
    /// </summary>
    private static void ValidateResolutionOrder(TenancySettings options, List<string> failures)
    {
        foreach (var strategy in options.EffectiveResolutionOrder)
        {
            if (strategy == TenantResolutionStrategy.Host)
            {
                failures.Add(
                    "Tenancy:ResolutionOrder includes \"Host\", which is defined but not implemented. "
                    + "Use \"Claim\" and/or \"Header\".");
            }
        }
    }

    /// <summary>
    /// Checks one tenant's overrides: each must name a real physical source and carry a connection
    /// string for the engine that source runs on.
    /// </summary>
    private void ValidateTenant(string tenantId, TenantEntrySettings tenant, List<string> failures)
    {
        foreach (var (sourceName, over) in tenant.DataSources)
        {
            var engines = DeclaredEngines(over);
            if (engines.Count == 0)
            {
                failures.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Tenancy:Tenants:{0}:DataSources:{1} declares no connection string. "
                    + "A per-tenant override must set SQLServerConnectionString, SqliteConnectionString "
                    + "or CosmosConnectionString; remove the entry to keep the source shared.",
                    tenantId,
                    sourceName));
                continue;
            }

            if (resolver is null)
            {
                continue;
            }

            foreach (var engine in engines.Where(engine => !IsKnownPhysicalSource(engine, sourceName)))
            {
                failures.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Tenancy:Tenants:{0}:DataSources:{1} overrides a {2} data source that does not exist. "
                    + "Per-tenant overrides are keyed by PHYSICAL data source name (\"{3}\", or a name "
                    + "declared under the DataSources section).",
                    tenantId,
                    sourceName,
                    engine,
                    DataSourceKey.DefaultName));
            }
        }
    }

    /// <summary>The engines this override actually declares a connection string for.</summary>
    private static List<DataSource> DeclaredEngines(TenantDataSourceOverrideSettings over) =>
        [.. Engines.Where(engine => !string.IsNullOrWhiteSpace(ConnectionStringFor(engine, over)))];

    /// <summary>
    /// Whether <paramref name="sourceName"/> is a physical source name for the engine. An unknown
    /// logical name resolves to <c>Default</c>, so a name that does not round-trip was never a
    /// physical source to begin with.
    /// </summary>
    private bool IsKnownPhysicalSource(DataSource engine, string sourceName) =>
        string.Equals(sourceName, DataSourceKey.DefaultName, StringComparison.Ordinal)
        || string.Equals(resolver!.ResolveLogical(engine, sourceName).Name, sourceName, StringComparison.Ordinal);

    /// <summary>The override's connection string for one engine, or null when it declares none.</summary>
    internal static string? ConnectionStringFor(DataSource engine, TenantDataSourceOverrideSettings over) =>
        engine switch
        {
            DataSource.SQLServer => over.SQLServerConnectionString,
            DataSource.Sqlite => over.SqliteConnectionString,
            DataSource.CosmosDB => over.CosmosConnectionString,
            _ => null,
        };
}
