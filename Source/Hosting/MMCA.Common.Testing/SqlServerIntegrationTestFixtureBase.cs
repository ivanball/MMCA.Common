using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Respawn;
using Xunit;

namespace MMCA.Common.Testing;

/// <summary>
/// Shared scaffolding for the per-service integration-test fixtures: boots a service host in-process
/// via the subclass-supplied <see cref="CreateFactory"/> backed by a <b>throwaway SQL Server
/// database</b> — LocalDB by default; override the server portion via the
/// <see cref="SqlBaseEnvironmentVariable"/> environment variable for CI (e.g. a SQL service
/// container). The schema is applied on first start (the host's <c>DatabaseInitStrategy=Migrate</c>
/// runs the module's migrations against the fresh database), data is reset between tests with Respawn
/// (<see cref="ResetDatabaseAsync"/>), and the database is dropped on disposal.
/// <para>
/// Overrides are pushed through <b>process environment variables</b> set before the host is built,
/// because the host reads its connection string / settings at configure-time. The environment is
/// forced to <c>Testing</c> so <c>appsettings.Development.json</c> (which points the module's
/// <c>DataSources</c> entry at <c>localhost</c>) does not load; with only <c>appsettings.json</c> (no
/// <c>DataSources</c> section) the resolver collapses onto the overridden top-level connection string.
/// Subclasses push their host-specific settings (test JWT keys, throttle lifts, faked gRPC edges) via
/// <see cref="ConfigureTestEnvironment"/>; every pushed variable is restored on disposal.
/// </para>
/// </summary>
/// <typeparam name="TEntryPoint">The service host's entry-point type (its <c>Program</c> class).</typeparam>
public abstract class SqlServerIntegrationTestFixtureBase<TEntryPoint> : IAsyncLifetime, IIntegrationTestFixture
    where TEntryPoint : class
{
    private readonly Dictionary<string, string?> _originalEnvironment = [];

    private string _serverBase = string.Empty;
    private string _databaseName = string.Empty;
    private WebApplicationFactory<TEntryPoint>? _factory;
    private Respawner? _respawner;
    private bool _databaseCreated;

    /// <summary>The HTTP client created when the host first started (used for fixture setup).</summary>
    public HttpClient? Client { get; private set; }

    /// <summary>
    /// Connection string for the throwaway test database — exposed so SQL-fidelity tests can read the
    /// raw tables directly (e.g. to assert an integration event landed in the outbox).
    /// </summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// The booted host's root service provider — exposed so cross-service tests can resolve a
    /// consumer-side <c>IIntegrationEventHandler&lt;T&gt;</c> (or a repository) and drive / assert the
    /// integration-event flow directly against the real database.
    /// </summary>
    public IServiceProvider Services => _factory!.Services;

    /// <summary>
    /// Name of the environment variable holding the SQL Server base connection string for CI (e.g.
    /// <c>ADC_TEST_SQL_BASE</c>). When unset, LocalDB is used.
    /// </summary>
    protected abstract string SqlBaseEnvironmentVariable { get; }

    /// <summary>Prefix for the throwaway database name (e.g. <c>ADC_IdentityIntegrationTest</c>).</summary>
    protected abstract string DatabaseNamePrefix { get; }

    /// <inheritdoc />
    public HttpClient CreateClient() => _factory!.CreateClient();

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _serverBase = Environment.GetEnvironmentVariable(SqlBaseEnvironmentVariable)
            ?? @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
        _databaseName = $"{DatabaseNamePrefix}_{Guid.NewGuid():N}";
        ConnectionString = $"{_serverBase}Database={_databaseName};";

        // ASPNETCORE_ENVIRONMENT=Testing so appsettings.Development.json (localhost DataSources) is skipped.
        SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        SetEnvironmentVariable("ConnectionStrings__SQLServerConnectionString", ConnectionString);
        ConfigureTestEnvironment(SetEnvironmentVariable);

        _factory = CreateFactory();

        // Creating the client forces the host to build and run Program.cs's
        // InitializeDatabaseAsync (Migrate creates the database + applies the module's migrations).
        Client = _factory.CreateClient();
        _databaseCreated = true;

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.SqlServer,
            TablesToIgnore = ["__EFMigrationsHistory"],
        });
    }

    /// <inheritdoc />
    public async Task ResetDatabaseAsync()
    {
        if (_respawner is null)
        {
            return;
        }

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await _respawner.ResetAsync(connection);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        Client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_databaseCreated)
        {
            await DropDatabaseAsync();
        }

        RestoreEnvironment();
    }

    /// <summary>Creates the <c>WebApplicationFactory</c> that boots the service host in-process.</summary>
    protected abstract WebApplicationFactory<TEntryPoint> CreateFactory();

    /// <summary>
    /// Pushes additional host settings as process environment variables before the host is built
    /// (test JWT key material, rate-limit lifts, faked cross-service edges, ...). The base has already
    /// set <c>ASPNETCORE_ENVIRONMENT=Testing</c> and the top-level SQL connection string; every
    /// variable pushed through <paramref name="setEnvironmentVariable"/> is restored on disposal.
    /// </summary>
    protected virtual void ConfigureTestEnvironment(Action<string, string?> setEnvironmentVariable)
    {
    }

    private void SetEnvironmentVariable(string key, string? value)
    {
        // Record only the FIRST original value so re-pushing a key cannot clobber the restore point.
        if (!_originalEnvironment.ContainsKey(key))
        {
            _originalEnvironment[key] = Environment.GetEnvironmentVariable(key);
        }

        Environment.SetEnvironmentVariable(key, value);
    }

    private void RestoreEnvironment()
    {
        foreach (var (key, value) in _originalEnvironment)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _originalEnvironment.Clear();
    }

    private async Task DropDatabaseAsync()
    {
        // Release pooled connections so the database is free to drop.
        SqlConnection.ClearAllPools();

        await using var connection = new SqlConnection($"{_serverBase}Database=master;");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // _databaseName is a server-generated GUID (Guid.NewGuid), never user input; DB names can't be parameterized.
        command.CommandText =
            $"IF DB_ID(N'{_databaseName}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{_databaseName}]; END";
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }
}
