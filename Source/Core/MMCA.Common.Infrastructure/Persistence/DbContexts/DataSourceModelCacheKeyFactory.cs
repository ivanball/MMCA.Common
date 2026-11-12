using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts;

/// <summary>
/// Model cache key factory that keys EF's model cache by everything an <see cref="ApplicationDbContext"/>
/// model depends on beyond its type: the physical data source name, the settings-gated tables
/// (<see cref="ApplicationDbContext.ModelGateMask"/>) and the declared strongly typed identifiers
/// (<see cref="ApplicationDbContext.StronglyTypedIdModelKey"/>). Required because one context class per
/// engine (e.g. <see cref="SQLServerDbContext"/>) is instantiated once per physical database, each with a
/// different EF model containing only that database's entities, and because the gated tables are mapped
/// only when their gate is on.
/// <para>
/// Without the source name, the first-built model would silently be reused for every physical source, so
/// queries would target tables that don't exist in the other databases. Without the gates, a process
/// that builds two differently gated models (design-time model tests, for one) would get whichever was
/// built first for both. EF builds the key after <c>OnConfiguring</c> has run on the same instance (this
/// factory is itself installed there), so the gates it reads are final.
/// </para>
/// </summary>
public sealed class DataSourceModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc />
    public object Create(DbContext context, bool designTime) =>
        context is ApplicationDbContext applicationDbContext
            ? (context.GetType(),
                applicationDbContext.DataSourceKey.Name,
                applicationDbContext.ModelGateMask,
                applicationDbContext.StronglyTypedIdModelKey,
                designTime)
            : (context.GetType(), designTime);
}
