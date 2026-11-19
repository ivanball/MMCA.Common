using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace MMCA.Common.Aspire.Health;

/// <summary>
/// Readiness check for one PostgreSQL database: opens a connection and runs <c>SELECT 1</c>, both under
/// the check's cancellation token, on the SAME <see cref="NpgsqlDataSource"/> the PostgreSQL contexts use
/// whenever the host registered one.
/// <para>
/// MMCA.Common.Infrastructure registers that data source as a keyed singleton, keyed by the connection
/// string, once a data-source configurator is added (RDS IAM tokens through
/// <c>AddAwsRdsIamAuthentication</c>, for one). A check built from the bare connection string would
/// then authenticate without the password provider and report a healthy database as down. This
/// package cannot reference Infrastructure, so the contract is an Npgsql type plus a string, decided
/// per probe: the registrations can arrive in either order.
/// </para>
/// <para>
/// With no keyed data source the check probes a data source of its own, built from the connection
/// string on first use, kept for every later probe (so its pool carries the reuse) and disposed with
/// this check, which the container holds as a singleton. The shared data source is never disposed
/// here: the container that registered it owns it.
/// </para>
/// </summary>
internal sealed class PostgreSqlHealthCheck : IHealthCheck, IDisposable, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly IServiceProvider _services;
    private readonly Lock _sync = new();
    private NpgsqlDataSource? _ownedDataSource;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="PostgreSqlHealthCheck"/> class.</summary>
    /// <param name="connectionString">The PostgreSQL connection string, also the shared data-source key.</param>
    /// <param name="services">The container the shared data source is looked up in on every probe.</param>
    public PostgreSqlHealthCheck(string connectionString, IServiceProvider services)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(services);

        _connectionString = connectionString;
        _services = services;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var connection = await DataSourceForProbe().OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.CommandText = "SELECT 1;";
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A health check reports a failure, never throws: an unreachable database turns readiness unhealthy
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                description: "PostgreSQL did not answer SELECT 1.",
                exception: ex);
        }
    }

    /// <inheritdoc />
    public void Dispose() => TakeOwnedDataSourceForDisposal()?.Dispose();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var owned = TakeOwnedDataSourceForDisposal();
        if (owned is not null)
        {
            await owned.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The data source the next probe runs on: the shared keyed one when the container holds it,
    /// otherwise the one this check owns. Decided on every call.
    /// </summary>
    /// <returns>The data source to open a connection from.</returns>
    /// <exception cref="ObjectDisposedException">The check was disposed and no shared data source is registered.</exception>
    internal NpgsqlDataSource DataSourceForProbe() =>
        SharedDataSource() ?? OwnedDataSource();

    private NpgsqlDataSource? SharedDataSource() =>
        _services is IKeyedServiceProvider keyed
            ? keyed.GetKeyedService(typeof(NpgsqlDataSource), _connectionString) as NpgsqlDataSource
            : null;

    private NpgsqlDataSource OwnedDataSource()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _ownedDataSource ??= NpgsqlDataSource.Create(_connectionString);
        }
    }

    private NpgsqlDataSource? TakeOwnedDataSourceForDisposal()
    {
        lock (_sync)
        {
            _disposed = true;
            var owned = _ownedDataSource;
            _ownedDataSource = null;
            return owned;
        }
    }
}
