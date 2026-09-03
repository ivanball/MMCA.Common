using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;

namespace MMCA.Common.Infrastructure.Persistence.DataSources;

/// <summary>
/// Singleton implementation of <see cref="IDataSourceResolver"/>. Builds the logical→physical map
/// once from <see cref="ConnectionStringSettings"/> and <see cref="DataSourcesSettings"/>,
/// validating that no two logical names that collapse to the same database declare conflicting
/// migrations assemblies. Either section can supply the <c>Default</c> source on its own: the
/// top-level one when it names a connection, otherwise the single database the named entries declare
/// (see <c>ResolveDefaultSeed</c>).
/// </summary>
public sealed partial class DataSourceResolver : IDataSourceResolver
{
    private static readonly DataSource[] AllEngines = [DataSource.CosmosDB, DataSource.Sqlite, DataSource.SQLServer];

    /// <summary>
    /// Engine preference used to pick the substitute engine for a request naming an engine the host
    /// does not configure. Relational first, because every table the framework owns (outbox, inbox,
    /// scheduled jobs, audit trail, refresh sessions) is relational, and SQL Server ahead of SQLite
    /// so a host that configures SQL Server at all keeps exactly the routing it had before.
    /// </summary>
    private static readonly DataSource[] EnginePreference = [DataSource.SQLServer, DataSource.Sqlite, DataSource.CosmosDB];

    /// <summary>Per-engine map of logical name → physical key (collapse already applied).</summary>
    private readonly Dictionary<(DataSource Engine, string LogicalName), DataSourceKey> _logicalToPhysical = [];

    /// <summary>Resolved connection information per physical key (Default keys always present).</summary>
    private readonly Dictionary<DataSourceKey, PhysicalDataSource> _physicalSources = [];

    /// <summary>Engines carrying at least one connection string (top-level or on a named entry).</summary>
    private readonly HashSet<DataSource> _configuredEngines = [];

    /// <summary>
    /// The engine a request for an unconfigured engine is served from, or <see langword="null"/>
    /// when the host configures no database at all (nothing to substitute, so requests pass through
    /// exactly as they did before).
    /// </summary>
    private readonly DataSource? _substituteEngine;

    /// <summary>
    /// Initializes the resolver, eagerly building and validating the logical→physical map.
    /// </summary>
    /// <param name="connectionStringOptions">The top-level connection strings (the Default source).</param>
    /// <param name="dataSources">The named data source entries.</param>
    /// <param name="logger">Logger for configuration warnings.</param>
    /// <exception cref="InvalidOperationException">
    /// Two logical names collapse to the same physical database but declare different
    /// <c>SQLServerMigrationsAssembly</c> values.
    /// </exception>
    public DataSourceResolver(
        IOptions<ConnectionStringSettings> connectionStringOptions,
        DataSourcesSettings dataSources,
        ILogger<DataSourceResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionStringOptions);
        ArgumentNullException.ThrowIfNull(dataSources);

        var connectionStrings = connectionStringOptions.Value;

        foreach (var engine in AllEngines)
        {
            BuildEngineMap(engine, connectionStrings, dataSources, logger);

            if (HasAnyConnectionString(engine, connectionStrings, dataSources))
            {
                _configuredEngines.Add(engine);
            }
        }

        _substituteEngine = EnginePreference
            .Where(_configuredEngines.Contains)
            .Select(engine => (DataSource?)engine)
            .FirstOrDefault();

        if (_substituteEngine is { } substitute && substitute != DataSource.SQLServer)
        {
            // Worth one startup line: the framework's own tables (outbox, inbox, scheduled jobs,
            // audit trail) default to SQL Server in settings, and this is where a host that
            // configures no SQL Server connection learns they were served from another engine.
            LogSubstituteEngine(logger, substitute);
        }
    }

    /// <inheritdoc />
    public DataSourceKey ResolveLogical(DataSource engine, string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        var effectiveEngine = SubstituteUnconfiguredEngine(engine);

        if (string.Equals(logicalName, DataSourceKey.DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            return DataSourceKey.Default(effectiveEngine);
        }

        return _logicalToPhysical.TryGetValue((effectiveEngine, logicalName), out var key)
            ? key
            : DataSourceKey.Default(effectiveEngine);
    }

    /// <summary>
    /// Substitutes the host's own engine for a request naming an engine it does not configure.
    /// <para>
    /// Every engine choice the framework makes for its own tables comes from a setting that defaults
    /// to <see cref="DataSource.SQLServer"/> (<c>Outbox:DataSource</c>, <c>Scheduler:DataSource</c>,
    /// <c>AuditTrail:DataSource</c>). Honoring that default literally in a host that configures only
    /// SQLite handed those components a physical source with an empty connection string, and the
    /// first query they ran failed with "The ConnectionString property has not been initialized".
    /// Serving them from the engine the host actually configures is what makes a single-engine host
    /// work without every framework component growing its own engine setting.
    /// </para>
    /// <para>
    /// Nothing moves for a host that configures the requested engine, so a SQL-Server-only host and a
    /// polyglot host that configures both engines (ADR-018) resolve exactly as before; only a request
    /// that could not have been served at all is redirected.
    /// </para>
    /// </summary>
    /// <param name="engine">The requested engine.</param>
    /// <returns>The requested engine, or the substitute when it is unconfigured.</returns>
    private DataSource SubstituteUnconfiguredEngine(DataSource engine) =>
        _configuredEngines.Contains(engine) ? engine : _substituteEngine ?? engine;

    /// <summary>
    /// Reports whether the engine carries a connection string anywhere in configuration: the
    /// top-level <c>ConnectionStrings</c> section or any named <c>DataSources</c> entry.
    /// </summary>
    private static bool HasAnyConnectionString(
        DataSource engine,
        ConnectionStringSettings connectionStrings,
        DataSourcesSettings dataSources) =>
        !string.IsNullOrEmpty(GetConnectionString(engine, connectionStrings))
        || dataSources.Sources.Values.Any(entry => !string.IsNullOrEmpty(GetConnectionString(engine, entry)));

    /// <inheritdoc />
    public PhysicalDataSource GetPhysical(DataSourceKey key) =>
        _physicalSources.TryGetValue(key, out var physical)
            ? physical
            : throw new InvalidOperationException(
                $"No physical data source is configured for \"{key}\". " +
                "Physical keys must be obtained via IDataSourceResolver.ResolveLogical.");

    /// <summary>
    /// Builds the logical→physical map and physical source registry for one engine:
    /// collapses entries onto Default (no/equal connection string), groups entries sharing a
    /// connection onto one canonical physical source, and validates migrations assemblies.
    /// </summary>
    private void BuildEngineMap(
        DataSource engine,
        ConnectionStringSettings connectionStrings,
        DataSourcesSettings dataSources,
        ILogger<DataSourceResolver> logger)
    {
        var seed = ResolveDefaultSeed(engine, connectionStrings, dataSources);
        var (defaultCollapsed, groups) = ClassifyEntries(engine, seed, dataSources);
        RegisterDefaultSource(engine, seed, defaultCollapsed);

        foreach (var members in groups.Values)
        {
            RegisterNamedSource(engine, seed, members, logger);
        }
    }

    /// <summary>
    /// What the <c>Default</c> physical source for one engine is built from.
    /// </summary>
    /// <param name="ConnectionString">The connection the Default source uses, empty when the engine is unconfigured.</param>
    /// <param name="MigrationsAssembly">The migrations assembly declared for it, empty when none is.</param>
    /// <param name="CosmosDatabaseName">The Cosmos database name entries fall back to.</param>
    private sealed record DefaultSeed(string ConnectionString, string MigrationsAssembly, string CosmosDatabaseName);

    /// <summary>
    /// Resolves what the engine's <c>Default</c> source is built from. The top-level
    /// <c>ConnectionStrings</c> section is the first answer; when it names nothing for this engine
    /// and the <c>DataSources</c> section names exactly ONE database on it, that database IS the
    /// host's single database and becomes Default.
    /// <para>
    /// That second branch is what lets a host declare its database only under <c>DataSources</c>:
    /// the framework's own tables (outbox, inbox, scheduled jobs, audit trail) resolve to the
    /// <c>Default</c> logical name, so without it they would resolve to a source with an empty
    /// connection string and fail on their first query. It cannot change an existing host's routing,
    /// because it only applies where the top-level value is absent.
    /// </para>
    /// <para>
    /// With SEVERAL distinct databases and no top-level value there is no single answer, so Default
    /// stays empty: a genuinely multi-database host names the one it wants shared by adding a
    /// <c>DataSources:Default</c> entry, which is just another entry as far as the collapse is
    /// concerned.
    /// </para>
    /// </summary>
    /// <param name="engine">The engine whose Default source is being built.</param>
    /// <param name="connectionStrings">The top-level connection strings.</param>
    /// <param name="dataSources">The named data source entries.</param>
    /// <returns>The connection, migrations assembly and Cosmos database the Default source uses.</returns>
    private static DefaultSeed ResolveDefaultSeed(
        DataSource engine,
        ConnectionStringSettings connectionStrings,
        DataSourcesSettings dataSources)
    {
        var topLevel = GetConnectionString(engine, connectionStrings);
        if (!string.IsNullOrEmpty(topLevel))
        {
            return new DefaultSeed(
                topLevel,
                // The top-level migrations assembly is SQL Server's alone: ConnectionStrings carries
                // no SQLite equivalent, and handing a SQLite Default source the SQL Server value in a
                // mixed-engine host would scaffold the wrong schema.
                engine == DataSource.SQLServer ? connectionStrings.SQLServerMigrationsAssembly : string.Empty,
                connectionStrings.CosmosDatabaseName);
        }

        var candidates = dataSources.Sources.Values
            .Where(entry => !string.IsNullOrEmpty(GetConnectionString(engine, entry)))
            .ToList();

        var distinctIdentities = candidates
            .Select(entry => GetIdentity(engine, GetConnectionString(engine, entry), CosmosDatabaseNameOf(entry, connectionStrings)))
            .Distinct(StringComparer.Ordinal)
            .Count();

        // The migrations assembly is deliberately not seeded here: every one of these entries has
        // the seed's own connection identity, so they all collapse onto Default and contribute their
        // declared assemblies through AddExplicitMigrationsAssemblies, conflicts included.
        return distinctIdentities == 1
            ? new DefaultSeed(
                GetConnectionString(engine, candidates[0]),
                string.Empty,
                CosmosDatabaseNameOf(candidates[0], connectionStrings))
            : new DefaultSeed(string.Empty, string.Empty, connectionStrings.CosmosDatabaseName);
    }

    /// <summary>The Cosmos database an entry uses: its own name, or the top-level one.</summary>
    private static string CosmosDatabaseNameOf(DataSourceEntrySettings entry, ConnectionStringSettings connectionStrings) =>
        string.IsNullOrEmpty(entry.CosmosDatabaseName) ? connectionStrings.CosmosDatabaseName : entry.CosmosDatabaseName;

    /// <summary>
    /// Classifies each named entry for the engine: collapsed onto Default (no connection string,
    /// or connection identity equal to the Default source's) versus grouped by connection identity.
    /// </summary>
    private static (List<(string LogicalName, DataSourceEntrySettings Entry)> DefaultCollapsed,
        Dictionary<string, List<(string LogicalName, DataSourceEntrySettings Entry)>> Groups) ClassifyEntries(
        DataSource engine,
        DefaultSeed seed,
        DataSourcesSettings dataSources)
    {
        var defaultIdentity = GetIdentity(engine, seed.ConnectionString, seed.CosmosDatabaseName);
        var defaultCollapsed = new List<(string LogicalName, DataSourceEntrySettings Entry)>();
        var groups = new Dictionary<string, List<(string LogicalName, DataSourceEntrySettings Entry)>>(StringComparer.Ordinal);

        foreach (var (logicalName, entry) in dataSources.Sources)
        {
            var entryConnection = GetConnectionString(engine, entry);
            if (string.IsNullOrEmpty(entryConnection))
            {
                // No connection string for this engine — falls back to Default. No mapping entry
                // needed: ResolveLogical already defaults on a map miss.
                continue;
            }

            var entryCosmosDb = string.IsNullOrEmpty(entry.CosmosDatabaseName)
                ? seed.CosmosDatabaseName
                : entry.CosmosDatabaseName;
            var identity = GetIdentity(engine, entryConnection, entryCosmosDb);

            if (string.Equals(identity, defaultIdentity, StringComparison.Ordinal))
            {
                defaultCollapsed.Add((logicalName, entry));
                continue;
            }

            if (!groups.TryGetValue(identity, out var members))
            {
                members = [];
                groups[identity] = members;
            }

            members.Add((logicalName, entry));
        }

        return (defaultCollapsed, groups);
    }

    /// <summary>
    /// Registers the Default physical source for the engine. Entries collapsed onto it may
    /// contribute an explicit migrations assembly; conflicting explicit values throw.
    /// </summary>
    private void RegisterDefaultSource(
        DataSource engine,
        DefaultSeed seed,
        List<(string LogicalName, DataSourceEntrySettings Entry)> defaultCollapsed)
    {
        var defaultKey = DataSourceKey.Default(engine);

        var explicitValues = new List<(string LogicalName, string Assembly)>();

        // The seed's value is already scoped to this engine (see ResolveDefaultSeed), so it can be
        // added as-is; an entry collapsing onto Default that repeats the same value is deduplicated
        // by ResolveMigrationsAssembly.
        if (!string.IsNullOrEmpty(seed.MigrationsAssembly))
        {
            explicitValues.Add((DataSourceKey.DefaultName, seed.MigrationsAssembly));
        }

        AddExplicitMigrationsAssemblies(engine, explicitValues, defaultCollapsed);

        _physicalSources[defaultKey] = BuildPhysicalSource(
            engine,
            defaultKey,
            seed.ConnectionString,
            ResolveMigrationsAssembly(engine, defaultKey, explicitValues),
            seed.CosmosDatabaseName);

        foreach (var (logicalName, _) in defaultCollapsed)
        {
            _logicalToPhysical[(engine, logicalName)] = defaultKey;
        }
    }

    /// <summary>
    /// Registers one named physical source for a group of entries sharing a connection identity,
    /// named after the alphabetically-first member for determinism.
    /// </summary>
    private void RegisterNamedSource(
        DataSource engine,
        DefaultSeed seed,
        List<(string LogicalName, DataSourceEntrySettings Entry)> members,
        ILogger<DataSourceResolver> logger)
    {
        var canonicalName = members.Select(m => m.LogicalName).Order(StringComparer.Ordinal).First();
        var key = new DataSourceKey(engine, canonicalName);

        var explicitValues = new List<(string LogicalName, string Assembly)>();
        AddExplicitMigrationsAssemblies(engine, explicitValues, members);

        var migrationsAssembly = ResolveMigrationsAssembly(engine, key, explicitValues);
        if (engine == DataSource.SQLServer && migrationsAssembly is null)
        {
            // Falling back to the Default migrations assembly is almost always a mistake for a
            // separate database (its snapshot describes a different schema) — surface it.
            migrationsAssembly = string.IsNullOrEmpty(seed.MigrationsAssembly)
                ? null
                : seed.MigrationsAssembly;
            LogMigrationsAssemblyFallback(logger, key.Name, migrationsAssembly ?? "<context assembly>");
        }

        var canonicalEntry = members.First(m => string.Equals(m.LogicalName, canonicalName, StringComparison.Ordinal)).Entry;
        var cosmosDatabaseName = string.IsNullOrEmpty(canonicalEntry.CosmosDatabaseName)
            ? seed.CosmosDatabaseName
            : canonicalEntry.CosmosDatabaseName;

        _physicalSources[key] = BuildPhysicalSource(
            engine,
            key,
            GetConnectionString(engine, canonicalEntry),
            migrationsAssembly,
            cosmosDatabaseName);

        foreach (var (logicalName, _) in members)
        {
            _logicalToPhysical[(engine, logicalName)] = key;
        }
    }

    private static void AddExplicitMigrationsAssemblies(
        DataSource engine,
        List<(string LogicalName, string Assembly)> explicitValues,
        List<(string LogicalName, DataSourceEntrySettings Entry)> members)
    {
        foreach (var (logicalName, entry) in members)
        {
            var assembly = GetMigrationsAssembly(engine, entry);
            if (!string.IsNullOrEmpty(assembly))
            {
                explicitValues.Add((logicalName, assembly));
            }
        }
    }

    /// <summary>
    /// Places the resolved migrations assembly in the slot of the engine it belongs to. A physical
    /// source is per-engine, so at most one of the two properties is ever populated; keeping them
    /// apart is what stops a SQL Server assembly from being handed to <c>UseSqlite</c>.
    /// </summary>
    private static PhysicalDataSource BuildPhysicalSource(
        DataSource engine,
        DataSourceKey key,
        string connectionString,
        string? migrationsAssembly,
        string cosmosDatabaseName) =>
        new(
            key,
            connectionString,
            engine == DataSource.SQLServer ? migrationsAssembly : null,
            cosmosDatabaseName)
        {
            SqliteMigrationsAssembly = engine == DataSource.Sqlite ? migrationsAssembly : null,
        };

    /// <summary>
    /// The per-engine migrations assembly declared on one <c>DataSources</c> entry. Only the
    /// relational engines have one; Cosmos migrates nothing.
    /// </summary>
    private static string GetMigrationsAssembly(DataSource engine, DataSourceEntrySettings entry) => engine switch
    {
        DataSource.CosmosDB => string.Empty,
        DataSource.Sqlite => entry.SqliteMigrationsAssembly,
        DataSource.SQLServer => entry.SQLServerMigrationsAssembly,
        _ => string.Empty,
    };

    /// <summary>
    /// Picks the single explicit migrations assembly for a physical source, throwing when logical
    /// names sharing the database declare conflicting values. Cosmos sources have none.
    /// </summary>
    private static string? ResolveMigrationsAssembly(
        DataSource engine,
        DataSourceKey key,
        List<(string LogicalName, string Assembly)> explicitValues)
    {
        if (engine == DataSource.CosmosDB || explicitValues.Count == 0)
        {
            return null;
        }

        var distinct = explicitValues.Select(v => v.Assembly).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 1)
        {
            var settingName = engine == DataSource.Sqlite ? "SqliteMigrationsAssembly" : "SQLServerMigrationsAssembly";
            var declarations = string.Join("; ", explicitValues.Select(v => $"\"{v.LogicalName}\" → \"{v.Assembly}\""));
            throw new InvalidOperationException(
                $"Data sources collapsing to the same physical database \"{key}\" declare conflicting " +
                $"{settingName} values: {declarations}. Align them on a single assembly.");
        }

        return distinct[0];
    }

    /// <summary>
    /// Computes the physical-identity string for a connection. Cosmos identities include the
    /// database name because one Cosmos account hosts many databases; relational engines use the
    /// connection string alone. Comparison is ordinal — semantically-equal-but-textually-different
    /// connection strings deliberately do not collapse.
    /// </summary>
    private static string GetIdentity(DataSource engine, string connectionString, string cosmosDatabaseName) =>
        engine == DataSource.CosmosDB
            ? string.Concat(connectionString, "\n", cosmosDatabaseName)
            : connectionString;

    private static string GetConnectionString(DataSource engine, ConnectionStringSettings settings) => engine switch
    {
        DataSource.CosmosDB => settings.CosmosConnectionString,
        DataSource.Sqlite => settings.SqliteConnectionString,
        DataSource.SQLServer => settings.SQLServerConnectionString,
        _ => throw new InvalidOperationException($"DataSource \"{engine}\" not implemented."),
    };

    private static string GetConnectionString(DataSource engine, DataSourceEntrySettings entry) => engine switch
    {
        DataSource.CosmosDB => entry.CosmosConnectionString,
        DataSource.Sqlite => entry.SqliteConnectionString,
        DataSource.SQLServer => entry.SQLServerConnectionString,
        _ => throw new InvalidOperationException($"DataSource \"{engine}\" not implemented."),
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "No SQL Server connection string is configured; data sources requesting an unconfigured engine (including the framework's own outbox, inbox, scheduled-job, and audit-trail tables) resolve to {SubstituteEngine}.")]
    private static partial void LogSubstituteEngine(ILogger logger, DataSource substituteEngine);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SQL Server data source \"{DataSourceName}\" has no dedicated SQLServerMigrationsAssembly; falling back to {Fallback}. Applying another database's migrations to a separate database is almost always a mistake — declare a per-source migrations assembly.")]
    private static partial void LogMigrationsAssemblyFallback(ILogger logger, string dataSourceName, string fallback);
}
