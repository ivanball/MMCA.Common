namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Seeding;

/// <summary>
/// Contract for module-specific database seeders. Each module implements this to populate
/// its tables with initial reference data at application startup.
/// </summary>
public interface IDbSeeder
{
    /// <summary>
    /// Seeds the database with initial data for the implementing module.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SeedAsync(CancellationToken cancellationToken);
}
