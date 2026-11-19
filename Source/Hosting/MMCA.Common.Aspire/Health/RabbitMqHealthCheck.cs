using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MMCA.Common.Aspire.Health;

/// <summary>
/// Reachability check for the RabbitMQ broker: takes the connection <see cref="RabbitMqHealthConnection"/>
/// holds and opens, then closes, one channel on it, under the check's cancellation token.
/// <para>
/// The connection is never opened or disposed here. The holder keeps at most one AMQP connection for the
/// whole process and bounds its handshake by its own timeout, so a broker port that accepts the TCP
/// connection and never answers ends the creation even when no probe is waiting any more; this check's
/// token only stops this probe waiting for it.
/// </para>
/// </summary>
internal sealed class RabbitMqHealthCheck : IHealthCheck
{
    private readonly RabbitMqHealthConnection _connection;

    /// <summary>Initializes a new instance of the <see cref="RabbitMqHealthCheck"/> class.</summary>
    /// <param name="connection">The holder of the one connection the check probes through.</param>
    public RabbitMqHealthCheck(RabbitMqHealthConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var connection = await _connection.GetAsync(cancellationToken).ConfigureAwait(false);

            var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await channel.DisposeAsync().ConfigureAwait(false);

            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A health check reports a failure, never throws: this optional check degrades /health instead
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                description: "RabbitMQ did not open a channel.",
                exception: ex);
        }
    }
}
