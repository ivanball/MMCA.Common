namespace MMCA.Common.Infrastructure.Persistence.Tenancy;

/// <summary>
/// How <c>TenantResolutionMiddleware</c> looks for the tenant on an inbound request.
/// </summary>
public enum TenantResolutionStrategy
{
    /// <summary>
    /// Read the tenant from a claim on the authenticated principal
    /// (<see cref="TenancySettings.ClaimType"/>). The trustworthy source: the claim was signed by
    /// the token issuer, so a caller cannot pick its own tenant.
    /// </summary>
    Claim = 0,

    /// <summary>
    /// Read the tenant from a request header (<see cref="TenancySettings.HeaderName"/>). Intended
    /// for service-to-service calls behind a trusted gateway; a public edge that honors this header
    /// lets any caller name any tenant.
    /// </summary>
    Header = 1,

    /// <summary>
    /// Derive the tenant from the request host name. <b>Defined but not implemented.</b> The member
    /// exists so the configuration contract is stable when host-based resolution ships; selecting it
    /// today fails options validation at startup rather than silently resolving nothing.
    /// </summary>
    Host = 2,
}

/// <summary>
/// Configuration for multi-tenancy, bound from the <c>Tenancy</c> section by
/// <c>AddMultiTenancy(configuration)</c>. Every property has a default, so a host that opts in only
/// needs <c>Tenancy:Enabled</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Enabled"/> gates <b>resolution</b>, not isolation. The query filter and the save
/// interceptor are always present and are inert whenever no tenant is resolved, so a host that
/// never enables this keeps exactly the behavior it had before tenancy shipped.
/// </para>
/// <para>
/// <b>Collection defaults are expressed as "empty means the default".</b>
/// <see cref="ResolutionOrder"/> and <see cref="ExcludedPathPrefixes"/> bind as empty lists and the
/// framework reads <see cref="EffectiveResolutionOrder"/> / <see cref="EffectiveExcludedPathPrefixes"/>
/// instead. The configuration binder ADDS to a pre-populated collection rather than replacing it, so
/// a non-empty default would leave a host that configured its own order running the framework's
/// entries as well.
/// </para>
/// </remarks>
public sealed class TenancySettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "Tenancy";

    /// <summary>The resolution order used when <see cref="ResolutionOrder"/> is left empty.</summary>
    private static readonly TenantResolutionStrategy[] DefaultResolutionOrder =
        [TenantResolutionStrategy.Claim, TenantResolutionStrategy.Header];

    /// <summary>The excluded prefixes used when <see cref="ExcludedPathPrefixes"/> is left empty.</summary>
    private static readonly string[] DefaultExcludedPathPrefixes =
        ["/health", "/alive", "/.well-known"];

    /// <summary>
    /// Gets a value indicating whether tenant resolution runs. Defaults to <see langword="false"/>:
    /// adopting the framework must never start rejecting requests that carry no tenant.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the strategies tried, in order, until one yields a tenant. Empty means
    /// <see cref="EffectiveResolutionOrder"/> (claim, then header).
    /// </summary>
    public List<TenantResolutionStrategy> ResolutionOrder { get; } = [];

    /// <summary>Gets the resolution order actually used, falling back to the framework default.</summary>
    public IReadOnlyList<TenantResolutionStrategy> EffectiveResolutionOrder =>
        ResolutionOrder.Count == 0 ? DefaultResolutionOrder : ResolutionOrder;

    /// <summary>
    /// Gets the claim type carrying the tenant on the authenticated principal. Defaults to
    /// <c>tenant_id</c>.
    /// </summary>
    public string ClaimType { get; init; } = "tenant_id";

    /// <summary>
    /// Gets the request header carrying the tenant. Defaults to <c>X-Tenant-Id</c>. Only honored
    /// when <see cref="TenantResolutionStrategy.Header"/> is in the resolution order.
    /// </summary>
    public string HeaderName { get; init; } = "X-Tenant-Id";

    /// <summary>
    /// Gets a value indicating whether a request that resolves no tenant is rejected with
    /// <c>400 Bad Request</c>. Defaults to <see langword="true"/>: with tenancy switched on, an
    /// unscoped request would read across every tenant, so the safe answer is to fail closed.
    /// </summary>
    public bool RequireTenant { get; init; } = true;

    /// <summary>
    /// Gets the request path prefixes that bypass resolution entirely (probes and discovery
    /// documents, which are not tenant-scoped and must answer before any tenant exists). Empty
    /// means <see cref="EffectiveExcludedPathPrefixes"/>.
    /// </summary>
    public List<string> ExcludedPathPrefixes { get; } = [];

    /// <summary>Gets the excluded prefixes actually used, falling back to the framework default.</summary>
    public IReadOnlyList<string> EffectiveExcludedPathPrefixes =>
        ExcludedPathPrefixes.Count == 0 ? DefaultExcludedPathPrefixes : ExcludedPathPrefixes;

    /// <summary>
    /// Gets the declared tenants, keyed by tenant identifier and bound from
    /// <c>Tenancy:Tenants:{tenantId}</c>. Declaring a tenant is only required for the
    /// database-per-tenant case: shared-schema tenants need no entry, because isolation there comes
    /// from the query filter and the save interceptor rather than from configuration.
    /// </summary>
    public Dictionary<string, TenantEntrySettings> Tenants { get; } = [];
}

/// <summary>
/// One declared tenant, bound from <c>Tenancy:Tenants:{tenantId}</c>.
/// </summary>
public sealed class TenantEntrySettings
{
    /// <summary>
    /// Gets this tenant's per-data-source connection overrides, keyed by <b>physical</b> data
    /// source name (the name <c>IDataSourceResolver</c> produces, e.g. <c>Default</c> or
    /// <c>Conference</c>). A source with an entry here is routed to the tenant's own database; a
    /// source without one stays shared and is isolated by the query filter.
    /// </summary>
    public Dictionary<string, TenantDataSourceOverrideSettings> DataSources { get; } = [];
}

/// <summary>
/// A tenant's connection override for one physical data source, bound from
/// <c>Tenancy:Tenants:{tenantId}:DataSources:{sourceName}</c>. Exactly the connection information
/// the framework needs to clone the resolved <c>PhysicalDataSource</c>; the migrations assembly is
/// deliberately not overridable, since a tenant database has the same schema as the shared one.
/// </summary>
public sealed class TenantDataSourceOverrideSettings
{
    /// <summary>Gets the SQL Server connection string for this tenant's copy of the source.</summary>
    public string? SQLServerConnectionString { get; init; }

    /// <summary>Gets the SQLite connection string for this tenant's copy of the source.</summary>
    public string? SqliteConnectionString { get; init; }

    /// <summary>Gets the Azure Cosmos DB connection string for this tenant's copy of the source.</summary>
    public string? CosmosConnectionString { get; init; }

    /// <summary>
    /// Gets the Cosmos DB database name for this tenant. Optional: when omitted the shared source's
    /// database name is kept, which is how one Cosmos account serves per-tenant accounts.
    /// </summary>
    public string? CosmosDatabaseName { get; init; }
}
