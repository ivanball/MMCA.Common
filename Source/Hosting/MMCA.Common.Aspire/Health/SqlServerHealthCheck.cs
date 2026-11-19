using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MMCA.Common.Aspire.Health;

/// <summary>
/// Readiness check for one SQL Server database: opens a connection and runs <c>SELECT 1</c>, both
/// under the check's cancellation token.
/// <para>
/// Each probe opens its own <see cref="SqlConnection"/>, so the driver's connection pool (keyed by the
/// connection string) carries the reuse; the check holds no state and is safe to share across probes.
/// The driver is the same <c>Microsoft.Data.SqlClient</c> build MMCA.Common.Infrastructure connects
/// with, so a connection string that authenticates for the contexts authenticates here too.
/// </para>
/// </summary>
internal sealed class SqlServerHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    /// <summary>Initializes a new instance of the <see cref="SqlServerHealthCheck"/> class.</summary>
    /// <param name="connectionString">The SQL Server connection string to probe.</param>
    public SqlServerHealthCheck(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var connection = new SqlConnection(_connectionString);
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

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
                description: "SQL Server did not answer SELECT 1.",
                exception: ex);
        }
    }
}
