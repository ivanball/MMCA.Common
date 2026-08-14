using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Repositories;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Tenancy;

/// <summary>
/// End-to-end coverage for the named <c>Tenant</c> global query filter over SQLite: it composes with
/// <c>SoftDelete</c>, it is inert for a system context, and one cached model serves two tenants at
/// once with disjoint results.
/// </summary>
public sealed class ApplicationDbContextTenantFilterTests : IDisposable
{
    private const string Acme = "acme";
    private const string Globex = "globex";

    private static readonly string[] SoftDeleteOnly = [ApplicationDbContext.SoftDeleteFilterName];

    private readonly SqliteConnection _connection = TenantTestContext.OpenDatabase();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task TenantFilter_HidesOtherTenantsRows()
    {
        await SeedAsync();

        await using var acme = TenantTestContext.Create(_connection, () => Acme);

        var visible = await acme.Things.AsNoTracking().Select(t => t.Name).ToListAsync();

        string[] expected = ["acme-live"];
        visible.Should().BeEquivalentTo(expected,
            "the Tenant filter scopes reads to the resolved tenant and SoftDelete removes the deleted row");
    }

    [Fact]
    public async Task TenantFilter_ComposesWithSoftDelete_RatherThanReplacingIt()
    {
        await SeedAsync();

        await using var acme = TenantTestContext.Create(_connection, () => Acme);

        // Dropping ONLY the soft-delete filter: the deleted row comes back, the other tenant's does not.
        var withDeleted = await acme.Things
            .IgnoreQueryFilters(SoftDeleteOnly)
            .AsNoTracking()
            .Select(t => t.Name)
            .ToListAsync();

        string[] expected = ["acme-live", "acme-deleted"];
        withDeleted.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task NullTenant_SeesEveryTenantsRows()
    {
        await SeedAsync();

        await using var system = TenantTestContext.Create(_connection, tenantAccessor: null);

        var visible = await system.Things.AsNoTracking().Select(t => t.Name).ToListAsync();

        string[] expected = ["acme-live", "globex-live"];
        visible.Should().BeEquivalentTo(expected,
            "a background service resolves no tenant and must see every tenant's rows");
    }

    // -- The load-bearing one: two tenants, two contexts, ONE compiled model --
    [Fact]
    public async Task TwoTenants_ShareOneCachedModel_AndStillReadDisjointRows()
    {
        await SeedAsync();

        await using var acme = TenantTestContext.Create(_connection, () => Acme);
        await using var globex = TenantTestContext.Create(_connection, () => Globex);

        acme.Model.Should().BeSameAs(globex.Model,
            "the model cache is keyed by (context type, physical source), so the tenant must ride in as a "
            + "query parameter rather than forcing a model per tenant");

        var acmeRows = await acme.Things.AsNoTracking().Select(t => t.Name).ToListAsync();
        var globexRows = await globex.Things.AsNoTracking().Select(t => t.Name).ToListAsync();

        string[] acmeExpected = ["acme-live"];
        string[] globexExpected = ["globex-live"];
        acmeRows.Should().BeEquivalentTo(acmeExpected);
        globexRows.Should().BeEquivalentTo(globexExpected);
        acmeRows.Should().NotIntersectWith(globexRows);
    }

    [Fact]
    public async Task LiveAccessor_IsReadAtQueryTime_NotAtContextCreation()
    {
        await SeedAsync();

        string? tenant = null;
        await using var context = TenantTestContext.Create(_connection, () => tenant);

        // Created with no tenant, exactly as it would be when the context is materialized before the
        // resolving middleware has run.
        (await context.Things.AsNoTracking().CountAsync()).Should().Be(2);

        tenant = Globex;

        var rows = await context.Things.AsNoTracking().Select(t => t.Name).ToListAsync();

        string[] expected = ["globex-live"];
        rows.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task RepositoryIgnoreQueryFilters_IncludesDeleted_ButKeepsTheTenantFilter()
    {
        await SeedAsync();

        await using var acme = TenantTestContext.Create(_connection, () => Acme);
        var repository = new EFReadRepository<TenantThing, int>(acme);

        var rows = await repository.GetAllAsync([], ignoreQueryFilters: true);

        string[] expected = ["acme-live", "acme-deleted"];
        rows.Select(r => r.Name).Should().BeEquivalentTo(expected,
            "ignoreQueryFilters means 'include soft-deleted', never 'include other tenants'");
    }

    [Fact]
    public async Task NonTenantEntities_AreUnaffected()
    {
        await SeedAsync();

        await using var acme = TenantTestContext.Create(_connection, () => Acme);

        (await acme.PlainThings.AsNoTracking().CountAsync())
            .Should().Be(1, "an entity that never carried ITenantEntity gets no Tenant filter");
    }

    // -- Model shape --
    [Fact]
    public void TenantIdProperty_IsRequiredNonUnicodeAndCapped()
    {
        using var context = TenantTestContext.Create(_connection);

        var property = context.Model.FindEntityType(typeof(TenantThing))!
            .FindProperty(ApplicationDbContext.TenantIdPropertyName)!;

        property.IsNullable.Should().BeFalse();
        property.GetMaxLength().Should().Be(ApplicationDbContext.TenantIdMaxLength);
        property.IsUnicode().Should().BeFalse();
    }

    [Fact]
    public void TenantIdColumn_IsIndexed()
    {
        using var context = TenantTestContext.Create(_connection);

        context.Model.FindEntityType(typeof(TenantThing))!
            .GetIndexes()
            .Should().Contain(index =>
                index.Properties.Count == 1
                && index.Properties[0].Name == ApplicationDbContext.TenantIdPropertyName,
                "every tenant-scoped read leads with TenantId, so an unindexed column is a scan per query");
    }

    [Fact]
    public void OwnedTypes_GetNoFilterOfTheirOwn()
    {
        using var context = TenantTestContext.Create(_connection);

        var owned = context.Model.GetEntityTypes().Single(t => t.ClrType == typeof(TenantDetail));

        owned.IsOwned().Should().BeTrue();
        owned.FindDeclaredQueryFilter(ApplicationDbContext.TenantFilterName).Should().BeNull(
            "an owned type is queried through its owner and inherits the owner's filter");
    }

    [Fact]
    public void BothNamedFilters_AreDeclaredOnATenantOwnedAuditableEntity()
    {
        using var context = TenantTestContext.Create(_connection);

        var entityType = context.Model.FindEntityType(typeof(TenantThing))!;

        entityType.FindDeclaredQueryFilter(ApplicationDbContext.TenantFilterName).Should().NotBeNull();
        entityType.FindDeclaredQueryFilter(ApplicationDbContext.SoftDeleteFilterName).Should().NotBeNull();
    }

    /// <summary>
    /// Seeds two tenants from a system (untenanted) context: one live row each, plus one
    /// soft-deleted row for the first tenant.
    /// </summary>
    private async Task SeedAsync()
    {
        await using var system = TenantTestContext.Create(_connection, tenantAccessor: null);

        var deleted = new TenantThing { Id = 2, TenantId = Acme, Name = "acme-deleted" };

        system.Things.AddRange(
            new TenantThing { Id = 1, TenantId = Acme, Name = "acme-live" },
            deleted,
            new TenantThing { Id = 3, TenantId = Globex, Name = "globex-live" });
        system.PlainThings.Add(new PlainThing { Id = 1, Name = "plain" });
        await system.SaveChangesAsync(null);

        deleted.Delete().IsSuccess.Should().BeTrue();
        await system.SaveChangesAsync(null);
    }
}
