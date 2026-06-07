using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts;

/// <summary>
/// Model cache key factory that keys EF's model cache by (context type, physical data source name)
/// instead of context type alone. Required because one context class per engine
/// (e.g. <see cref="SQLServerDbContext"/>) is instantiated once per physical database, each with a
/// different EF model containing only that database's entities.
/// <para>
/// Without this, the first-built model would silently be reused for every physical source —
/// queries would target tables that don't exist in the other databases.
/// </para>
/// </summary>
public sealed class DataSourceModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc />
    public object Create(DbContext context, bool designTime) =>
        context is ApplicationDbContext applicationDbContext
            ? (context.GetType(), applicationDbContext.DataSourceKey.Name, designTime)
            : (context.GetType(), designTime);
}
