using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.FeatureFlags;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.FeatureFlags;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.Infrastructure.Persistence.FeatureFlags;

/// <summary>
/// EF Core <see cref="IFeatureFlagOverrideStore"/> over the owning service's own database (ADR-031).
/// <para>
/// <b>Which database.</b> The table is opted into one model (see
/// <see cref="FeatureFlagOverrideModelBuilderExtensions"/>), so this resolves the physical source the
/// way <c>EFPermissionGrantStore</c> does: the data-source registry first, falling back to the source
/// named by <c>FeatureFlags:DataSourceName</c>.
/// </para>
/// <para>
/// <b>Writes go through the auditable entity.</b> A set loads the row (a cleared one included, so it
/// is revived rather than shadowed by a second row under the same key), changes it through the
/// entity's own validation and saves through the unit of work, so the audit interceptor stamps the
/// author and the instant. A clear soft-deletes the row for the same reason.
/// </para>
/// </summary>
/// <param name="dbContextFactory">Resolves and saves through the owning service's context.</param>
/// <param name="registry">Entity-to-source registry, consulted first.</param>
/// <param name="dataSourceResolver">Resolves the logical default source's engine.</param>
/// <param name="settings">Bound feature-flag override settings.</param>
internal sealed class EFFeatureFlagOverrideStore(
    IDbContextFactory dbContextFactory,
    IEntityDataSourceRegistry registry,
    IDataSourceResolver dataSourceResolver,
    IOptions<FeatureFlagOverrideSettings> settings) : IFeatureFlagOverrideStore
{
    private ApplicationDbContext Context => dbContextFactory.GetDbContext(ResolveDataSourceKey());

    private DbSet<FeatureFlagOverride> Overrides => Context.Set<FeatureFlagOverride>();

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeatureFlagOverrideResponse>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // The explicit IsDeleted predicate is redundant with the soft-delete filter on the
        // production model, and keeps a host that maps the table by hand (no filter) correct too.
        var rows = await Overrides
            .AsNoTracking()
            .Where(o => !o.IsDeleted)
            .OrderBy(o => o.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(ToResponse)];
    }

    /// <inheritdoc />
    public async Task<Result<FeatureFlagOverrideResponse>> SetAsync(
        string name,
        SetFeatureFlagOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(request);

        var existing = await Overrides
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == name, cancellationToken)
            .ConfigureAwait(false);

        FeatureFlagOverride entity;
        if (existing is null)
        {
            var created = FeatureFlagOverride.Create(
                name, request.Mode, request.Reason, request.Percentage, request.Users, request.Groups);
            if (!created.TryGetValue(out var value))
            {
                return Result.Failure<FeatureFlagOverrideResponse>(created.Errors);
            }

            entity = value;
            await Overrides.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var changed = existing.Change(request.Mode, request.Reason, request.Percentage, request.Users, request.Groups);
            if (changed.IsFailure)
            {
                return Result.Failure<FeatureFlagOverrideResponse>(changed.Errors);
            }

            entity = existing;
        }

        await dbContextFactory.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(ToResponse(entity));
    }

    /// <inheritdoc />
    public async Task<Result> ClearAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var existing = await Overrides
            .FirstOrDefaultAsync(o => o.Id == name && !o.IsDeleted, cancellationToken)
            .ConfigureAwait(false);

        // Nothing to clear is the success an idempotent clear promises.
        if (existing is null)
        {
            return Result.Success();
        }

        var cleared = existing.Clear();
        if (cleared.IsFailure)
        {
            return cleared;
        }

        await dbContextFactory.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    private static FeatureFlagOverrideResponse ToResponse(FeatureFlagOverride entity) =>
        new(
            entity.Name,
            entity.Mode,
            entity.Percentage,
            entity.Users,
            entity.Groups,
            entity.Reason,
            entity.LastModifiedOn ?? entity.CreatedOn,
            entity.LastModifiedBy ?? entity.CreatedBy);

    // The configured NAME is used verbatim; only the engine goes through the resolver (the
    // EFPermissionGrantStore reasoning).
    private DataSourceKey ResolveDataSourceKey() =>
        registry.TryGetDataSourceKey(typeof(FeatureFlagOverride).FullName!, out var key)
            ? key
            : new DataSourceKey(
                dataSourceResolver.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName).Engine,
                settings.Value.DataSourceName);
}
