using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace MMCA.Common.Aspire.Health;

/// <summary>
/// Registers the PostgreSQL readiness check for one connection string so that it probes the SAME
/// <see cref="NpgsqlDataSource"/> the PostgreSQL contexts use whenever the host registered one.
/// <para>
/// MMCA.Common.Infrastructure registers that data source as a keyed singleton, keyed by the connection
/// string, once a data-source configurator is added (RDS IAM tokens through
/// <c>AddAwsRdsIamAuthentication</c>, for one). A check built from the bare connection string would
/// then authenticate without the password provider and report a healthy database as down. This
/// package cannot reference Infrastructure, so the contract is an Npgsql type plus a string, decided
/// per probe: the registrations can arrive in either order.
/// </para>
/// <para>
/// With no keyed data source the check is the package's own connection-string check, factory and all:
/// both registrations are produced by the package's <c>AddNpgSql</c> overloads, and only the choice
/// between them is added here.
/// </para>
/// </summary>
internal static class PostgreSqlHealthCheckRegistration
{
    /// <summary>Adds the readiness check for <paramref name="connectionString"/> under <paramref name="name"/>.</summary>
    /// <param name="healthChecks">The builder the check is added to.</param>
    /// <param name="connectionString">The PostgreSQL connection string, also the data-source key.</param>
    /// <param name="name">The check name.</param>
    internal static void Register(IHealthChecksBuilder healthChecks, string connectionString, string name)
    {
        var connectionStringCheck = Capture(healthChecks, b => b.AddNpgSql(connectionString, name: name));
        var dataSourceCheck = Capture(healthChecks, b => b.AddNpgSql(sp => SharedDataSource(sp, connectionString)!, name: name));

        var connectionStringFactory = connectionStringCheck.Factory;
        connectionStringCheck.Factory = sp => SharedDataSource(sp, connectionString) is null
            ? connectionStringFactory(sp)
            : dataSourceCheck.Factory(sp);

        healthChecks.Add(connectionStringCheck);
    }

    private static NpgsqlDataSource? SharedDataSource(IServiceProvider serviceProvider, string connectionString) =>
        serviceProvider is IKeyedServiceProvider keyed
            ? keyed.GetKeyedService(typeof(NpgsqlDataSource), connectionString) as NpgsqlDataSource
            : null;

    private static HealthCheckRegistration Capture(IHealthChecksBuilder healthChecks, Action<IHealthChecksBuilder> register)
    {
        var capture = new CapturingBuilder(healthChecks.Services);
        register(capture);
        return capture.Registration
            ?? throw new InvalidOperationException("AddNpgSql registered no health check.");
    }

    /// <summary>Holds the one registration an <c>AddNpgSql</c> overload produces instead of adding it.</summary>
    private sealed class CapturingBuilder(IServiceCollection services) : IHealthChecksBuilder
    {
        public HealthCheckRegistration? Registration { get; private set; }

        public IServiceCollection Services => services;

        public IHealthChecksBuilder Add(HealthCheckRegistration registration)
        {
            Registration = registration;
            return this;
        }
    }
}
