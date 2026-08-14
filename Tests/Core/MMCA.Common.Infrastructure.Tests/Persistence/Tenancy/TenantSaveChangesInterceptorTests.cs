using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Infrastructure.Persistence.Interceptors;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Tenancy;

/// <summary>
/// Coverage for the write side of tenancy: the interceptor stamps inserts, refuses anything that
/// would cross the tenant boundary, and stays out of the way of entities that never opted in.
/// </summary>
public sealed class TenantSaveChangesInterceptorTests : IDisposable
{
    private const string Acme = "acme";
    private const string Globex = "globex";

    private readonly SqliteConnection _connection = TenantTestContext.OpenDatabase();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Added_IsStampedWithTheScopesTenant()
    {
        await using var acme = TenantTestContext.Create(_connection, () => Acme);

        var thing = new TenantThing { Id = 1, Name = "new" };
        acme.Things.Add(thing);
        await acme.SaveChangesAsync(null);

        thing.TenantId.Should().Be(Acme, "the tenant is the framework's to assign, not the caller's");
    }

    [Fact]
    public async Task Added_WithNoResolvedTenantAndNoTenantOnTheEntity_Throws()
    {
        await using var system = TenantTestContext.Create(_connection, tenantAccessor: null);

        system.Things.Add(new TenantThing { Id = 1, Name = "orphan" });

        var act = async () => await system.SaveChangesAsync(null);

        (await act.Should().ThrowAsync<CrossTenantWriteException>())
            .Which.EntityType.Should().Contain(nameof(TenantThing));
    }

    [Fact]
    public async Task Added_WithNoResolvedTenantButAnExplicitTenant_IsWrittenAsIs()
    {
        await using var system = TenantTestContext.Create(_connection, tenantAccessor: null);

        system.Things.Add(new TenantThing { Id = 1, TenantId = Globex, Name = "seeded" });
        await system.SaveChangesAsync(null);

        (await system.Things.AsNoTracking().SingleAsync()).TenantId.Should().Be(Globex,
            "a seeder or a per-tenant job legitimately writes an explicitly tenanted row");
    }

    [Fact]
    public async Task Added_ForAnotherTenant_Throws()
    {
        await using var acme = TenantTestContext.Create(_connection, () => Acme);

        acme.Things.Add(new TenantThing { Id = 1, TenantId = Globex, Name = "smuggled" });

        var act = async () => await acme.SaveChangesAsync(null);

        var exception = (await act.Should().ThrowAsync<CrossTenantWriteException>()).Which;
        exception.CurrentTenantId.Should().Be(Acme);
        exception.EntityTenantId.Should().Be(Globex);
    }

    [Fact]
    public async Task Modified_OfAnotherTenantsRow_Throws()
    {
        await SeedAsync(Globex, id: 1, name: "theirs");

        await using var acme = TenantTestContext.Create(_connection, () => Acme);

        // Reached past the filter on purpose: this is exactly the caller the write guard exists for.
        var row = await acme.Things.IgnoreQueryFilters().SingleAsync();
        row.Name = "hijacked";

        var act = async () => await acme.SaveChangesAsync(null);

        (await act.Should().ThrowAsync<CrossTenantWriteException>())
            .Which.EntityTenantId.Should().Be(Globex);
    }

    [Fact]
    public async Task Deleted_OfAnotherTenantsRow_Throws()
    {
        await SeedAsync(Globex, id: 1, name: "theirs");

        await using var acme = TenantTestContext.Create(_connection, () => Acme);

        var row = await acme.Things.IgnoreQueryFilters().SingleAsync();
        acme.Things.Remove(row);

        var act = async () => await acme.SaveChangesAsync(null);

        await act.Should().ThrowAsync<CrossTenantWriteException>();
    }

    [Fact]
    public async Task Modified_ReassigningTheTenant_Throws()
    {
        await SeedAsync(Acme, id: 1, name: "mine");

        await using var acme = TenantTestContext.Create(_connection, () => Acme);

        var row = await acme.Things.SingleAsync();
        row.TenantId = Globex;

        var act = async () => await acme.SaveChangesAsync(null);

        (await act.Should().ThrowAsync<CrossTenantWriteException>())
            .Which.EntityTenantId.Should().Be(Globex);
    }

    [Fact]
    public async Task Modified_OfOwnRow_Succeeds()
    {
        await SeedAsync(Acme, id: 1, name: "mine");

        await using var acme = TenantTestContext.Create(_connection, () => Acme);

        var row = await acme.Things.SingleAsync();
        row.Name = "renamed";

        await acme.SaveChangesAsync(null);

        (await acme.Things.AsNoTracking().SingleAsync()).Name.Should().Be("renamed");
    }

    [Fact]
    public async Task NonTenantEntities_AreUntouched()
    {
        await using var acme = TenantTestContext.Create(_connection, () => Acme);

        acme.PlainThings.Add(new PlainThing { Id = 1, Name = "plain" });

        await acme.SaveChangesAsync(null);

        (await acme.PlainThings.AsNoTracking().CountAsync()).Should().Be(1,
            "the interceptor is inert for every entity that does not carry ITenantEntity");
    }

    [Fact]
    public async Task OwnedValues_AreNotStamped_AndDoNotBlockASystemInsert()
    {
        await using var system = TenantTestContext.Create(_connection, tenantAccessor: null);

        system.Things.Add(new TenantThing { Id = 1, TenantId = Acme, Name = "with-detail" });

        var act = async () => await system.SaveChangesAsync(null);

        await act.Should().NotThrowAsync(
            "an owned value has no independent existence, so its tenant is its owner's and the guard skips it");

        (await system.Things.AsNoTracking().SingleAsync()).Detail.TenantId.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutTheInterceptorRegistered_TheContextStillBuildsAndSaves()
    {
        await using var context = TenantTestContext.Create(
            _connection, tenantAccessor: null, registerTenantInterceptor: false);

        context.Things.Add(new TenantThing { Id = 1, TenantId = Acme, Name = "unguarded" });

        await context.SaveChangesAsync(null);

        (await context.Things.AsNoTracking().CountAsync()).Should().Be(1,
            "a design-time or directly-constructed context resolves the interceptor with GetService and finds none");
    }

    [Fact]
    public async Task AuditTrailRows_RecordTheTenant()
    {
        await using var acme = TenantTestContext.Create(
            _connection, () => Acme, registerTrailInterceptor: true);

        acme.TrailedThings.Add(new TrailedTenantThing { Id = 1, Name = "trailed" });
        await acme.SaveChangesAsync(null);

        var rows = await acme.TrailRows.AsNoTracking().ToListAsync();

        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(row => row.TenantId == Acme,
            "the trail column exists so a shared trail table can still be read back per tenant");
    }

    [Fact]
    public async Task AuditTrailRows_LeaveTheTenantNull_ForASystemSave()
    {
        await using var system = TenantTestContext.Create(
            _connection, tenantAccessor: null, registerTrailInterceptor: true);

        system.TrailedThings.Add(new TrailedTenantThing { Id = 1, TenantId = Acme, Name = "seeded" });
        await system.SaveChangesAsync(null);

        (await system.TrailRows.AsNoTracking().ToListAsync())
            .Should().OnlyContain(row => row.TenantId == null);
    }

    /// <summary>Writes one row for a tenant through a system context, bypassing the write guard.</summary>
    private async Task SeedAsync(string tenantId, int id, string name)
    {
        await using var system = TenantTestContext.Create(_connection, tenantAccessor: null);
        system.Things.Add(new TenantThing { Id = id, TenantId = tenantId, Name = name });
        await system.SaveChangesAsync(null);
    }
}
