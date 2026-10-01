namespace MMCA.Common.Infrastructure.Persistence.DataSources.Engines;

/// <summary>
/// How an engine maps and maintains the <c>RowVersion</c> optimistic-concurrency token; read by
/// <c>ApplicationDbContext.ConfigureConcurrencyTokens</c> (mapping) and
/// <c>AuditSaveChangesInterceptor</c> (stamping).
/// </summary>
internal enum RowVersionStrategy
{
    /// <summary>No concurrency token is configured (Cosmos skips <c>ConfigureConcurrencyTokens</c>).</summary>
    None,

    /// <summary>Server-generated <c>rowversion</c>: mapped with <c>IsRowVersion()</c>, never stamped (SQL Server).</summary>
    StoreGenerated,

    /// <summary>Plain concurrency token the audit interceptor stamps on every insert and update (PostgreSQL, SQLite).</summary>
    ClientStamped,
}
