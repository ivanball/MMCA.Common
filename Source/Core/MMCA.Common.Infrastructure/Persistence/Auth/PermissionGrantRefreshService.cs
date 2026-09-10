using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Permissions;

namespace MMCA.Common.Infrastructure.Persistence.Auth;

/// <summary>
/// Primes the stored-permission-grant snapshot at startup and rebuilds it on the configured interval,
/// so a grant added by another replica takes effect here without a restart.
/// </summary>
/// <remarks>
/// <para>
/// <b>A failed load is logged and retried, never fatal.</b> Authorization keeps working off the
/// compiled registry while the grant table is unreachable, so a database blip degrades the feature to
/// "no stored grants" rather than taking the host down. That direction is deliberate: the failure mode
/// is a denied request an operator can see, not a granted one nobody notices.
/// </para>
/// <para>
/// Registered only by <c>AddStoredPermissionGrants()</c>, so a host that never opts in never starts
/// it and never queries a table its model does not map.
/// </para>
/// </remarks>
/// <param name="cache">The snapshot to rebuild.</param>
/// <param name="settings">Bound settings (the rebuild interval).</param>
/// <param name="logger">Logger for the load diagnostics.</param>
/// <param name="timeProvider">
/// Clock for the interval; defaults to <see cref="TimeProvider.System"/> so tests can drive the
/// minute-scale loop deterministically.
/// </param>
internal sealed partial class PermissionGrantRefreshService(
    IPermissionGrantCache cache,
    IOptions<PermissionGrantSettings> settings,
    ILogger<PermissionGrantRefreshService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly PermissionGrantSettings _settings = settings.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_settings.CacheSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await cache.RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Do not catch general exception types: any load fault must degrade to "no stored grants", never stop the host.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogRefreshFailed(logger, ex);
            }

            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Could not reload the stored permission grants; authorization continues from the compiled registry and the previous snapshot until the next attempt.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);
}
