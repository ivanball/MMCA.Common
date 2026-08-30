using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

namespace MMCA.Common.Infrastructure.Persistence.Auth;

/// <summary>
/// Background service that hard-deletes spent <see cref="RefreshSession"/> rows: every session that
/// stopped being usable more than <see cref="RefreshSessionSettings.RetentionDays"/> ago.
/// <para>
/// <b>Why hard delete.</b> Sessions are framework bookkeeping, not an aggregate: the row carries no
/// soft-delete flag and no audit stamps, and its whole content is a credential digest plus the IP and
/// user-agent of the device that signed in. Keeping it past its usefulness is a growing table AND a
/// growing set of records describing a data subject's devices (ADR-005), so the sweep removes the row
/// rather than flagging it.
/// </para>
/// <para>
/// <b>Retention bounds reuse detection.</b> BR-206 catches a replayed refresh token by finding its
/// revoked row and revoking the whole family; a rotation chain (<c>ReplacedByTokenHash</c>) older than
/// the window is gone, so a replay of a token that old reads as an unknown token and fails alone
/// instead of signalling reuse. The default window (30 days) is far past the default refresh-token
/// lifetime (7 days), so every token still capable of being replayed still has its row. A host that
/// shortens <c>RefreshSessions:RetentionDays</c> below <c>Jwt:RefreshTokenExpirationDays</c> is
/// choosing to lose that signal.
/// </para>
/// <para>
/// <b>One database, unlike the outbox.</b> Sessions live in exactly one physical source (the outbox
/// lives in all of them), so the sweep resolves that single source the same way
/// <see cref="EFRefreshSessionStore"/> does: the entity registry first, then the source named by
/// <c>RefreshSessions:DataSourceName</c>. It is registered only when <c>RefreshSessions:Enabled</c>
/// is set, so a host that never mapped the table never starts it.
/// </para>
/// </summary>
/// <param name="scopeFactory">Factory for the DI scope each sweep resolves its context from.</param>
/// <param name="logger">Logger for sweep diagnostics; the per-sweep deleted count is logged here.</param>
/// <param name="options">Bound refresh-session settings (retention window and sweep interval).</param>
/// <param name="timeProvider">
/// Clock for the sweep interval and the retention cutoff; defaults to <see cref="TimeProvider.System"/>
/// so tests can drive the hour-scale loop deterministically.
/// </param>
public sealed partial class RefreshSessionCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<RefreshSessionCleanupService> logger,
    IOptions<RefreshSessionSettings> options,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly RefreshSessionSettings _settings = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Registration is already gated on Enabled; this second check is what keeps a host that
        // registers the service by hand from sweeping a table its model never mapped.
        if (!_settings.Enabled)
        {
            LogSessionsDisabled(logger);
            return;
        }

        if (_settings.RetentionDays <= 0)
        {
            LogCleanupDisabled(logger);
            return;
        }

        var interval = TimeSpan.FromHours(_settings.CleanupIntervalHours);

        // Wait one interval before the first sweep so cleanup never competes with startup or
        // migration work, then sweep on each interval until shutdown.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);
                await PurgeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogCleanupError(logger, ex);
            }
        }
    }

    /// <summary>
    /// Deletes every session that died before the cutoff. "Died" is the revocation instant when the
    /// row was revoked and the expiry instant when it aged out un-revoked, so a live session is never
    /// a candidate however old it is, and a session revoked minutes ago survives even if it expired
    /// long before (that recent revocation is exactly what reuse detection reads).
    /// </summary>
    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.Subtract(TimeSpan.FromDays(_settings.RetentionDays));

        using var scope = scopeFactory.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IEntityDataSourceRegistry>();
        var resolver = scope.ServiceProvider.GetRequiredService<IDataSourceResolver>();
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory>();
        var context = dbContextFactory.GetDbContext(ResolveDataSourceKey(registry, resolver));

        // The source can be reachable and still not map the table (a host that pointed
        // RefreshSessions:DataSourceName at the wrong database). Say so once per sweep rather than
        // failing with a translation error the operator has to decode.
        if (context.Model.FindEntityType(typeof(RefreshSession)) is null)
        {
            LogTableNotMapped(logger, _settings.DataSourceName);
            return;
        }

        var deleted = await context.Set<RefreshSession>()
            .Where(s => s.RevokedAt != null && s.RevokedAt < cutoff
                || s.RevokedAt == null && s.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        // Logged on every sweep, zero included: this line is the operator's evidence that retention
        // is running at all, which a "only when it deleted something" log cannot give.
        LogPurged(logger, deleted, _settings.RetentionDays);
    }

    /// <summary>
    /// Resolves the one physical source holding the table, matching
    /// <see cref="EFRefreshSessionStore"/> exactly so the sweep can never visit a different database
    /// than the store reads.
    /// </summary>
    private DataSourceKey ResolveDataSourceKey(IEntityDataSourceRegistry registry, IDataSourceResolver resolver) =>
        registry.TryGetDataSourceKey(typeof(RefreshSession).FullName!, out var key)
            ? key
            : new DataSourceKey(
                resolver.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName).Engine,
                _settings.DataSourceName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refresh-session cleanup not started: RefreshSessions:Enabled is false")]
    private static partial void LogSessionsDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refresh-session cleanup disabled: RefreshSessions:RetentionDays is 0")]
    private static partial void LogCleanupDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} refresh sessions that stopped being usable more than {RetentionDays} days ago")]
    private static partial void LogPurged(ILogger logger, int count, int retentionDays);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh-session cleanup found no RefreshSessions table on data source {DataSourceName}; check RefreshSessions:DataSourceName")]
    private static partial void LogTableNotMapped(ILogger logger, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Refresh-session cleanup encountered an error")]
    private static partial void LogCleanupError(ILogger logger, Exception exception);
}
