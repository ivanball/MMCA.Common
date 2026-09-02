using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace MMCA.Common.Aspire.Health;

/// <summary>
/// Reachability check for the Redis resource that issues nothing but <c>PING</c>.
/// <para>
/// It replaces the <c>AspNetCore.HealthChecks.Redis</c> check, which branches on the detected server
/// type and issues <c>CLUSTER INFO</c> against anything it considers clustered. Azure Managed Redis
/// (Enterprise tier, port 10000) is detected as a cluster by StackExchange.Redis 3.x and refuses
/// administrative commands unless the client opted into admin mode, so that check threw
/// <c>RedisCommandException</c> on every probe against a perfectly healthy cache. <c>PING</c> answers
/// the only question a health check has ("can this process talk to Redis right now?") and is never
/// gated on admin mode, on the server topology, or on the deployment tier.
/// </para>
/// <para>
/// The multiplexer is resolved from DI when the host wired Redis through
/// <c>AddRedisCaching()</c>. A host that configured a connection string without registering a client
/// gets a lazily created, connection-check-owned multiplexer instead, created once and disposed with
/// this singleton, so a health probe never opens a fresh connection per call.
/// </para>
/// </summary>
internal sealed class RedisPingHealthCheck : IHealthCheck, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly IServiceProvider _services;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private ConnectionMultiplexer? _ownedMultiplexer;

    public RedisPingHealthCheck(string connectionString, IServiceProvider services)
    {
        _connectionString = connectionString;
        _services = services;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            IConnectionMultiplexer multiplexer = _services.GetService<IConnectionMultiplexer>()
                ?? await GetOrCreateOwnedMultiplexerAsync().ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var latency = await multiplexer.GetDatabase().PingAsync().ConfigureAwait(false);

            return HealthCheckResult.Healthy(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Redis responded to PING in {0:F1} ms.",
                    latency.TotalMilliseconds));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Any failure to reach Redis is reported, never thrown: this check is tagged optional and
            // must degrade the /health payload rather than fault the probe pipeline.
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                description: "Redis did not respond to PING.",
                exception: ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownedMultiplexer is not null)
        {
            await _ownedMultiplexer.DisposeAsync().ConfigureAwait(false);
            _ownedMultiplexer = null;
        }

        _connectGate.Dispose();
    }

    // No lock-free fast path on purpose: health probes run on a timer, not on the request path, so an
    // uncontended semaphore wait costs nothing measurable and the single write keeps the "connect at
    // most once" invariant obvious. A failed connect leaves the field null, so the next probe retries
    // rather than latching a faulted result forever.
    private async Task<ConnectionMultiplexer> GetOrCreateOwnedMultiplexerAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return _ownedMultiplexer ??= await ConnectionMultiplexer.ConnectAsync(_connectionString).ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }
}
