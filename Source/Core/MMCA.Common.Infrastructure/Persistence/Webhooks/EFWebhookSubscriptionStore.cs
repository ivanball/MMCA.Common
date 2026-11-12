using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Domain.Webhooks;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

namespace MMCA.Common.Infrastructure.Persistence.Webhooks;

/// <summary>
/// EF Core <see cref="IWebhookSubscriptionStore"/> over the database <c>Webhooks:DataSourceName</c> names.
/// <para>
/// <b>Which database.</b> Resolved like <c>EFMachineClientStore</c>: the data-source registry first,
/// falling back to the configured source name on the framework-default engine.
/// </para>
/// <para>
/// <b>Tenant.</b> Every read filters <c>TenantId == tenantId</c> explicitly, a <see langword="null"/>
/// tenant matching only untenanted rows; the soft-delete filter hides deleted rows. Writes go through
/// the unit of work, so the audit interceptor stamps who created or changed a subscription.
/// </para>
/// </summary>
/// <param name="dbContextFactory">Resolves and saves through the subscription's context.</param>
/// <param name="registry">Entity-to-source registry, consulted first.</param>
/// <param name="dataSourceResolver">Resolves the logical default source's engine.</param>
/// <param name="settings">Bound webhook settings.</param>
/// <param name="concurrencyConflictDetector">Recognizes a lost race on the last-outcome columns.</param>
internal sealed class EFWebhookSubscriptionStore(
    IDbContextFactory dbContextFactory,
    IEntityDataSourceRegistry registry,
    IDataSourceResolver dataSourceResolver,
    IOptions<WebhookSettings> settings,
    IConcurrencyConflictDetector concurrencyConflictDetector) : IWebhookSubscriptionStore
{
    private ApplicationDbContext Context => dbContextFactory.GetDbContext(ResolveDataSourceKey());

    private DbSet<WebhookSubscription> Subscriptions => Context.Set<WebhookSubscription>();

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebhookSubscription>> ListAsync(string? tenantId, CancellationToken cancellationToken = default) =>
        await Subscriptions
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.CreatedOn)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebhookSubscription>> ListEnabledAsync(string? tenantId, CancellationToken cancellationToken = default) =>
        await Subscriptions
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.DisabledOn == null)
            .OrderBy(s => s.CreatedOn)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<WebhookSubscription?> FindAsync(Guid id, string? tenantId, CancellationToken cancellationToken = default) =>
        Subscriptions.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, cancellationToken);

    /// <inheritdoc />
    public Task<int> CountAsync(string? tenantId, CancellationToken cancellationToken = default) =>
        Subscriptions.CountAsync(s => s.TenantId == tenantId, cancellationToken);

    /// <inheritdoc />
    public async Task AddAsync(WebhookSubscription subscription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        await Subscriptions.AddAsync(subscription, cancellationToken).ConfigureAwait(false);
        await dbContextFactory.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveAsync(WebhookSubscription subscription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        // The row came from FindAsync, which tracks it, so the change is already known to the
        // context; the save is all that is left.
        await dbContextFactory.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordOutcomeAsync(WebhookSubscription subscription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        try
        {
            await dbContextFactory.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (concurrencyConflictDetector.IsConcurrencyConflict(exception))
        {
            // An administrative change or another delivery's outcome saved between this delivery's
            // read and its write (the row version moved). The delivery already happened, so the lost
            // last-outcome stamp is dropped rather than failing the row into a retry.
        }
    }

    // The configured NAME is used verbatim; only the engine goes through the resolver (the
    // EFMachineClientStore precedent).
    private DataSourceKey ResolveDataSourceKey() =>
        registry.TryGetDataSourceKey(typeof(WebhookSubscription).FullName!, out var key)
            ? key
            : new DataSourceKey(
                dataSourceResolver.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName).Engine,
                settings.Value.DataSourceName);
}
