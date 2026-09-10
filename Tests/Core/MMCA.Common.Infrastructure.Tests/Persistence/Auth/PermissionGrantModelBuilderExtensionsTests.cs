using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.Auth;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Auth;

/// <summary>
/// The opt-in permission-grant mapping, asserted against a built model rather than by reading the
/// configuration: the table is the one a consumer's migration generates, so its column set and its
/// unique (Role, Permission) index are a contract.
/// <para>
/// The mapping is also asserted against BOTH relational engines the framework maps tables on. That
/// is the point of the pairing: the extension declares no filtered-index predicate and no included
/// columns, which are the only two things <c>ApplicationDbContext.QuoteColumn</c> and
/// <c>IncludeColumns</c> exist to route per engine (ADR-113), so a divergence between the two models
/// here would mean the grant table had grown an engine-specific declaration that needs the same
/// routing the outbox indexes take.
/// </para>
/// </summary>
public sealed class PermissionGrantModelBuilderExtensionsTests
{
    /// <summary>A schema other than the default, to prove the parameter reaches the mapping.</summary>
    private const string CustomSchema = "identity";

    [Fact]
    public void ApplyPermissionGrantConfiguration_MapsTheTableKeyedOnItsId()
    {
        using var context = new GrantOnlySqlServerContext();

        var entity = context.Model.FindEntityType(typeof(PermissionGrant));

        entity.Should().NotBeNull();
        entity!.GetTableName().Should().Be(PermissionGrantModelBuilderExtensions.TableName);
        entity.FindPrimaryKey()!.Properties.Should().ContainSingle(p => p.Name == nameof(PermissionGrant.Id));
    }

    [Fact]
    public void ApplyPermissionGrantConfiguration_MapsExactlyTheStoredColumns()
    {
        using var context = new GrantOnlySqlServerContext();

        var columns = context.Model.FindEntityType(typeof(PermissionGrant))!
            .GetProperties()
            .Select(p => p.Name);

        columns.Should().BeEquivalentTo(
            nameof(PermissionGrant.Id),
            nameof(PermissionGrant.Role),
            nameof(PermissionGrant.Permission),
            nameof(PermissionGrant.GrantedAt),
            nameof(PermissionGrant.GrantedBy));
    }

    [Fact]
    public void ApplyPermissionGrantConfiguration_MakesRoleAndPermissionRequiredNonUnicodeColumns()
    {
        using var context = new GrantOnlySqlServerContext();
        var entity = context.Model.FindEntityType(typeof(PermissionGrant))!;

        var role = entity.FindProperty(nameof(PermissionGrant.Role))!;
        role.IsNullable.Should().BeFalse();
        role.GetMaxLength().Should().Be(PermissionGrant.RoleMaxLength);
        role.IsUnicode().Should().BeFalse();

        var permission = entity.FindProperty(nameof(PermissionGrant.Permission))!;
        permission.IsNullable.Should().BeFalse();
        permission.GetMaxLength().Should().Be(PermissionGrant.PermissionMaxLength);
        permission.IsUnicode().Should().BeFalse();
    }

    // Unique because a grant is a set membership, not a log: it is what makes an idempotent grant
    // safe under a concurrent duplicate insert, and what makes a revoke a single unambiguous row.
    [Fact]
    public void ApplyPermissionGrantConfiguration_IndexesRoleAndPermissionUniquely()
    {
        using var context = new GrantOnlySqlServerContext();

        var index = context.Model.FindEntityType(typeof(PermissionGrant))!
            .GetIndexes()
            .Single();

        index.IsUnique.Should().BeTrue();
        index.Properties.Select(p => p.Name).Should().Equal(
            nameof(PermissionGrant.Role),
            nameof(PermissionGrant.Permission));
        index.GetDatabaseName().Should().Be(PermissionGrantModelBuilderExtensions.RolePermissionIndexName);
    }

    [Fact]
    public void ApplyPermissionGrantConfiguration_HonorsACustomSchema()
    {
        using var context = new GrantOnlyCustomSchemaContext();

        context.Model.FindEntityType(typeof(PermissionGrant))!.GetSchema().Should().Be(CustomSchema);
    }

    // ── Engine parity (ADR-113) ──
    [Fact]
    public void ApplyPermissionGrantConfiguration_ProducesTheSameTableAndColumnsOnPostgreSQL()
    {
        using var sqlServer = new GrantOnlySqlServerContext();
        using var postgreSql = new GrantOnlyPostgreSqlContext();

        var fromSqlServer = sqlServer.Model.FindEntityType(typeof(PermissionGrant))!;
        var fromPostgreSql = postgreSql.Model.FindEntityType(typeof(PermissionGrant))!;

        fromPostgreSql.GetTableName().Should().Be(fromSqlServer.GetTableName());
        fromPostgreSql.GetSchema().Should().Be(fromSqlServer.GetSchema());
        fromPostgreSql.GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(fromSqlServer.GetProperties().Select(p => p.Name));
    }

    [Fact]
    public void ApplyPermissionGrantConfiguration_DeclaresNoEngineSpecificIndex()
    {
        // A filter predicate would carry a quoted identifier, and an INCLUDE list a provider-specific
        // annotation; either would have to go through the engine-routing helpers the outbox uses.
        // Declaring neither is what lets one configuration serve every relational engine.
        using var postgreSql = new GrantOnlyPostgreSqlContext();

        var index = postgreSql.Model.FindEntityType(typeof(PermissionGrant))!.GetIndexes().Single();

        index.GetFilter().Should().BeNull();
        index.GetAnnotations().Should().NotContain(
            a => a.Name.Contains("Include", StringComparison.Ordinal),
            "an included-column list is written per provider and would need the same routing the outbox indexes take");
    }

    [Fact]
    public void ApplyPermissionGrantConfiguration_LeavesTheTimestampColumnTypeToTheEngineConvention()
    {
        // PostgreSQLDbContext maps every DateTime to timestamptz through a model-wide
        // Properties<DateTime>() convention. Pinning a column type here would override it for this
        // table alone and hand Npgsql a value it refuses at save time.
        using var context = new GrantOnlySqlServerContext();

        var grantedAt = context.Model.FindEntityType(typeof(PermissionGrant))!
            .FindProperty(nameof(PermissionGrant.GrantedAt))!;

        grantedAt.FindAnnotation(RelationalAnnotationNames.ColumnType).Should().BeNull();
    }

    /// <summary>
    /// A bare context that maps nothing but the grant table, so the assertions see exactly what the
    /// extension configures. Never connects: model building needs no server.
    /// </summary>
    /// <remarks>
    /// The schema and the provider are baked into the TYPE rather than passed in, for the reason
    /// documented on <see cref="RefreshSessionModelBuilderExtensionsTests"/>: EF caches a built model
    /// per context type for the life of the process, so one type taking arguments would hand every
    /// case whichever model was built first and the assertions would pass or fail by test order.
    /// </remarks>
    private abstract class GrantOnlyContextBase(string? schema) : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ApplyPermissionGrantConfiguration(schema);
    }

    private sealed class GrantOnlySqlServerContext() : GrantOnlyContextBase("dbo")
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseSqlServer("Server=(local);Database=model-only;Trusted_Connection=True;");
    }

    private sealed class GrantOnlyCustomSchemaContext() : GrantOnlyContextBase(CustomSchema)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseSqlServer("Server=(local);Database=model-only;Trusted_Connection=True;");
    }

    private sealed class GrantOnlyPostgreSqlContext() : GrantOnlyContextBase("dbo")
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseNpgsql("Host=localhost;Database=model-only;Username=app");
    }
}
