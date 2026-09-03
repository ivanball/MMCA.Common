using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace MMCA.Common.Testing.Fixtures;

/// <summary>
/// One logical data source a cross-service fixture routes to its own physical database: the logical name
/// the framework's <c>DataSources</c> configuration section keys on (normally the module name) and the
/// database that name resolves to on the shared SQL Server container.
/// </summary>
/// <param name="LogicalName">The logical source name (e.g. <c>Conference</c>), used as the config key.</param>
/// <param name="DatabaseName">The physical database (e.g. <c>ADC_Conference</c>).</param>
public sealed record CrossServiceDataSource(string LogicalName, string DatabaseName);

/// <summary>
/// Shared scaffolding for the cross-service <b>real-broker</b> integration tier: boots several service hosts
/// in ONE process against a real Testcontainers SQL Server and a real Testcontainers RabbitMQ, so the
/// genuine outbox to broker to consumer round-trip (and any real cross-service gRPC read) is exercised end
/// to end. Unlike the per-service <see cref="SqlServerIntegrationTestFixtureBase{TEntryPoint}"/>, which
/// boots ONE host with the cross-service edges faked and no broker, this base owns the container lifecycle,
/// the pre-created databases, and the process-environment channel every host reads its settings from. The
/// per-app parts (which databases, how many hosts and in what order, which extra settings) stay in the
/// subclass through the hooks below.
/// <para><b>Config channel (the load-bearing design decision).</b> Each host reads its connection string,
/// <c>MessageBus</c> provider/connection string and JWT settings from <c>builder.Configuration</c> at
/// <i>configure-time</i>, BEFORE <c>builder.Build()</c>, which is before
/// <c>WebApplicationFactory.ConfigureAppConfiguration</c> deltas apply. So in-memory config injected via the
/// WAF would arrive too late; <b>process environment variables</b> are the only override channel these
/// hosts honour. Most keys are identical across hosts (broker provider/URI, dummy authority, test keys), so
/// they do not collide. The one genuinely per-host key is
/// <c>ConnectionStrings__SQLServerConnectionString</c> (each host's own database AND its outbox
/// <c>Default</c> source), which is why hosts must be booted <b>strictly sequentially</b> in
/// <see cref="BootHostsAsync"/>, calling <see cref="SetHostConnectionString"/> between boots: a host
/// snapshots its connection at boot (DataSource resolver, DbContext factory, outbox processor and the
/// MassTransit bus are all built during <c>StartAsync</c>), so mutating the environment for the next host
/// cannot disturb an already-booted one.
/// </para>
/// </summary>
public abstract class CrossServiceFixtureBase : IAsyncLifetime
{
    // Never fetched: the factories re-point real validation at the committed test key. It only has to be
    // present so AddForwardedJwtBearer's authority guard passes.
    private const string DummyBearerAuthority = "http://localhost";

    private readonly Dictionary<string, string?> _originalEnvironment = [];

    private MsSqlContainer? _sqlContainer;
    private RabbitMqContainer? _rabbitContainer;

    /// <summary>The Testcontainers RabbitMQ AMQP connection string wired into every host's broker.</summary>
    public string RabbitMqConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// The logical sources this tier routes to their own databases, in the order the databases are created.
    /// Drives both the pre-created database list and the per-module named-source routing described on
    /// <see cref="SetSharedEnvironment"/>.
    /// </summary>
    protected abstract IReadOnlyList<CrossServiceDataSource> DataSources { get; }

    /// <summary>
    /// Assembly-name prefix of the repo's per-module migrations projects (e.g.
    /// <c>MMCA.ADC.Migrations.SqlServer</c>); each named source gets
    /// <c>{prefix}.{LogicalName}</c> as its <c>SQLServerMigrationsAssembly</c>, matching production.
    /// </summary>
    protected abstract string MigrationsAssemblyPrefix { get; }

    /// <summary>The SQL Server container's base connection string (server, credentials, master catalog).</summary>
    protected string SqlServerBaseConnectionString =>
        (_sqlContainer ?? throw new InvalidOperationException("The SQL Server container has not started yet."))
            .GetConnectionString();

    /// <summary>
    /// Overlays a catalog (and optionally an Application Name) onto a base connection string. Pure and
    /// static so a fixture's connection-string composition is unit-testable without a container.
    /// </summary>
    /// <param name="baseConnectionString">The server's base connection string.</param>
    /// <param name="databaseName">The catalog to point at.</param>
    /// <param name="applicationName">Optional Application Name, used to keep a named source distinct.</param>
    /// <returns>The composed connection string.</returns>
    public static string ComposeConnectionString(string baseConnectionString, string databaseName, string? applicationName = null)
    {
        var builder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = databaseName,
            TrustServerCertificate = true,
        };

        if (!string.IsNullOrEmpty(applicationName))
        {
            builder.ApplicationName = applicationName;
        }

        return builder.ConnectionString;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        (_sqlContainer, _rabbitContainer) = CreateContainers();
        await Task.WhenAll(_sqlContainer.StartAsync(), _rabbitContainer.StartAsync()).ConfigureAwait(false);

        RabbitMqConnectionString = _rabbitContainer.GetConnectionString();
        await OnContainersStartedAsync().ConfigureAwait(false);

        // Pre-create the databases so no host's EF MigrateAsync issues CREATE DATABASE. CREATE DATABASE
        // runs BEFORE EF's migration lock (sp_getapplock) is acquired, so a host booted twice (the
        // real-Kestrel double-boot pattern) would otherwise race itself; with the databases pre-created EF
        // skips CREATE (Exists() is true) and the migration lock serializes the actual migration run.
        await CreateDatabasesAsync().ConfigureAwait(false);

        SetSharedEnvironment();

        await BootHostsAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        await DisposeHostsAsync().ConfigureAwait(false);

        if (_rabbitContainer is not null)
        {
            await _rabbitContainer.DisposeAsync().ConfigureAwait(false);
        }

        if (_sqlContainer is not null)
        {
            await _sqlContainer.DisposeAsync().ConfigureAwait(false);
        }

        RestoreEnvironment();
    }

    /// <summary>
    /// Boots the hosts, STRICTLY SEQUENTIALLY, calling <see cref="SetHostConnectionString"/> with each
    /// host's own database before creating it (plus any per-host key, e.g. a peer's real Kestrel address
    /// for a gRPC client). The host count and boot order are per-app, so they live here.
    /// </summary>
    protected abstract ValueTask BootHostsAsync();

    /// <summary>Disposes the hosts (and their clients) booted by <see cref="BootHostsAsync"/>.</summary>
    protected abstract ValueTask DisposeHostsAsync();

    /// <summary>
    /// Runs once both containers are up and <see cref="RabbitMqConnectionString"/> is known, before the
    /// databases are created. Override to cache the per-database connection strings the tests read.
    /// </summary>
    protected virtual ValueTask OnContainersStartedAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Pushes the app-specific shared settings (test JWT/JWKS key material, throttle lifts, third-party
    /// client stubs) after the base has pushed the environment, the named data sources, the broker and the
    /// dummy Bearer authority. Every variable pushed through <paramref name="setEnvironmentVariable"/> is
    /// restored on disposal.
    /// </summary>
    /// <param name="setEnvironmentVariable">The base's snapshot-taking environment setter.</param>
    protected virtual void ConfigureSharedEnvironment(Action<string, string?> setEnvironmentVariable)
    {
    }

    /// <summary>Composes a connection string for <paramref name="databaseName"/> on the SQL container.</summary>
    /// <param name="databaseName">The database to point at.</param>
    /// <returns>The composed connection string.</returns>
    protected string BuildConnectionString(string databaseName) =>
        ComposeConnectionString(SqlServerBaseConnectionString, databaseName);

    /// <summary>
    /// Points the shared top-level connection string (each host's own database AND its outbox
    /// <c>Default</c> source) at <paramref name="connectionString"/>. Call this immediately before booting
    /// each host: it is the one key that genuinely differs per host.
    /// </summary>
    /// <param name="connectionString">The next host's connection string.</param>
    protected void SetHostConnectionString(string connectionString) =>
        SetEnvironmentVariable("ConnectionStrings__SQLServerConnectionString", connectionString);

    /// <summary>
    /// Sets a process environment variable, snapshotting its ORIGINAL value for
    /// <see cref="DisposeAsync"/> to restore. Only the FIRST original value is recorded, so re-pushing a
    /// key (the per-host connection string) cannot clobber the restore point.
    /// </summary>
    /// <param name="key">The environment variable name.</param>
    /// <param name="value">The value to set, or <see langword="null"/> to clear it.</param>
    protected void SetEnvironmentVariable(string key, string? value)
    {
        if (!_originalEnvironment.ContainsKey(key))
        {
            _originalEnvironment[key] = Environment.GetEnvironmentVariable(key);
        }

        Environment.SetEnvironmentVariable(key, value);
    }

    // The parameterless module-builder ctors are [Obsolete] in Testcontainers 4.11 but fully functional
    // (each uses its module's own default pinned image). Testcontainers is version-pinned, so this is
    // stable; suppress the deprecation rather than couple to an image-ctor overload whose signature varies.
    // Built here rather than in a field initializer so a subclass can be constructed (and its non-container
    // logic unit-tested) on a machine with no Docker daemon.
#pragma warning disable CS0618
    private static (MsSqlContainer Sql, RabbitMqContainer Rabbit) CreateContainers() =>
        (new MsSqlBuilder().Build(), new RabbitMqBuilder().Build());
#pragma warning restore CS0618

    private async Task CreateDatabasesAsync()
    {
        var connection = new SqlConnection(BuildConnectionString("master"));
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            foreach (var databaseName in DataSources.Select(static source => source.DatabaseName))
            {
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
#pragma warning disable CA2100 // Database names are the subclass's own compile-time constants, never user input; DB names can't be parameterized.
                    command.CommandText = $"IF DB_ID(N'{databaseName}') IS NULL CREATE DATABASE [{databaseName}];";
#pragma warning restore CA2100
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private void SetSharedEnvironment()
    {
        SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        // Per-module NAMED data sources (the multi-host-in-one-process fix).
        // EF Core caches the model for a context type in a process-global service-provider cache.
        // DataSourceModelCacheKeyFactory keys it by (contextType, DataSourceKey.Name). If every host let
        // its entities collapse onto the "Default" source (as production does, one host per process), all
        // hosts here would share ONE cached model and the first booted would win, so the others could not
        // find their own entities. To keep each host's model distinct we route each module's entities to a
        // NAMED source by giving DataSources:{LogicalName} a connection string that differs ORDINALLY from
        // the top-level ConnectionStrings (same physical database, distinct Application Name) so the
        // resolver does NOT collapse it onto Default. These keys are module-namespaced, so they do not
        // collide across hosts and can all be set up front. Only the shared top-level ConnectionStrings key
        // is mutated per sequential boot. OutboxMessage / InboxMessage are configured in every relational
        // context, so a named source's model still has them.
        foreach (var source in DataSources)
        {
            SetNamedDataSource(source);
        }

        // Real broker transport (identical for every host, no collision).
        SetEnvironmentVariable("MessageBus__Provider", "RabbitMq");
        SetEnvironmentVariable("ConnectionStrings__rabbitmq", RabbitMqConnectionString);

        // Dummy authority so a host's AddForwardedJwtBearer does not throw on a missing
        // Authentication:JwtBearer:Authority. Real validation is re-pointed at the committed test key by
        // the factories (JwtTokenGenerator.ConfigureInProcessTokenValidation), so it is never fetched.
        SetEnvironmentVariable("Authentication__JwtBearer__Authority", DummyBearerAuthority);

        ConfigureSharedEnvironment(SetEnvironmentVariable);
    }

    // Routes a module's entities to a NAMED physical source pointing at its own database. The connection
    // string carries a distinct Application Name so it differs ordinally from the top-level ConnectionStrings
    // (same database) and the resolver keeps the "{LogicalName}" source instead of collapsing it onto
    // Default, giving each host a distinct EF model-cache key. The per-module migrations assembly matches
    // production.
    private void SetNamedDataSource(CrossServiceDataSource source)
    {
        var connectionString = ComposeConnectionString(
            SqlServerBaseConnectionString,
            source.DatabaseName,
            applicationName: $"MMCA-{source.LogicalName}");

        SetEnvironmentVariable($"DataSources__{source.LogicalName}__SQLServerConnectionString", connectionString);
        SetEnvironmentVariable(
            $"DataSources__{source.LogicalName}__SQLServerMigrationsAssembly",
            $"{MigrationsAssemblyPrefix}.{source.LogicalName}");
    }

    private void RestoreEnvironment()
    {
        foreach (var (key, value) in _originalEnvironment)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _originalEnvironment.Clear();
    }
}
