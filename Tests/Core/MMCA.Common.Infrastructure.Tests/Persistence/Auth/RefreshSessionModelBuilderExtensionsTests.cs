using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.Auth;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Auth;

/// <summary>
/// The opt-in refresh-session mapping, asserted against a built model rather than by reading the
/// configuration: the table is the one a consumer's migration generates, so its column set, the
/// unique token-hash index and the per-user index are a contract, and a computed member accidentally
/// becoming a column would only surface as a migration diff in a downstream repo.
/// </summary>
public sealed class RefreshSessionModelBuilderExtensionsTests
{
    /// <summary>A schema other than the default, to prove the parameter reaches the mapping.</summary>
    private const string CustomSchema = "identity";

    [Fact]
    public void ApplyRefreshSessionConfiguration_MapsTheTableKeyedOnItsId()
    {
        using var context = new RefreshSessionOnlyContext();

        var entity = context.Model.FindEntityType(typeof(RefreshSession));

        entity.Should().NotBeNull();
        entity!.GetTableName().Should().Be(RefreshSessionModelBuilderExtensions.TableName);
        entity.FindPrimaryKey()!.Properties.Should().ContainSingle(p => p.Name == nameof(RefreshSession.Id));
    }

    // The exact column set a consumer's generated migration will create. Computed members
    // (IsRevoked, IsActiveAt) must NOT appear: they are answers about the row, not columns in it.
    [Fact]
    public void ApplyRefreshSessionConfiguration_MapsExactlyTheStoredColumns()
    {
        using var context = new RefreshSessionOnlyContext();

        var columns = context.Model.FindEntityType(typeof(RefreshSession))!
            .GetProperties()
            .Select(p => p.Name);

        columns.Should().BeEquivalentTo(
            nameof(RefreshSession.Id),
            nameof(RefreshSession.UserId),
            nameof(RefreshSession.TokenHash),
            nameof(RefreshSession.CreatedAt),
            nameof(RefreshSession.ExpiresAt),
            nameof(RefreshSession.RevokedAt),
            nameof(RefreshSession.ReplacedByTokenHash),
            nameof(RefreshSession.ReasonRevoked),
            nameof(RefreshSession.IpAddress),
            nameof(RefreshSession.UserAgent));
    }

    [Fact]
    public void ApplyRefreshSessionConfiguration_MakesTheTokenHashAFixedWidthNonUnicodeRequiredColumn()
    {
        using var context = new RefreshSessionOnlyContext();

        var tokenHash = context.Model.FindEntityType(typeof(RefreshSession))!
            .FindProperty(nameof(RefreshSession.TokenHash))!;

        tokenHash.IsNullable.Should().BeFalse();
        tokenHash.GetMaxLength().Should().Be(RefreshSession.TokenHashLength);
        tokenHash.IsUnicode().Should().BeFalse();
        tokenHash.IsFixedLength().Should().BeTrue();
    }

    [Fact]
    public void ApplyRefreshSessionConfiguration_IndexesTheTokenHashUniquely()
    {
        using var context = new RefreshSessionOnlyContext();

        var index = context.Model.FindEntityType(typeof(RefreshSession))!
            .GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(RefreshSession.TokenHash));

        index.IsUnique.Should().BeTrue("two sessions sharing a hash would validate one user's token against another's session");
        index.GetDatabaseName().Should().Be(RefreshSessionModelBuilderExtensions.TokenHashIndexName);
    }

    [Fact]
    public void ApplyRefreshSessionConfiguration_IndexesTheFamilyLookup()
    {
        using var context = new RefreshSessionOnlyContext();

        var index = context.Model.FindEntityType(typeof(RefreshSession))!
            .GetIndexes()
            .Single(i => i.Properties.Count == 2);

        index.Properties.Select(p => p.Name).Should().Equal(nameof(RefreshSession.UserId), nameof(RefreshSession.RevokedAt));
        index.GetDatabaseName().Should().Be(RefreshSessionModelBuilderExtensions.UserIndexName);
    }

    [Fact]
    public void ApplyRefreshSessionConfiguration_HonorsACustomSchema()
    {
        using var context = new CustomSchemaContext();

        context.Model.FindEntityType(typeof(RefreshSession))!.GetSchema().Should().Be(CustomSchema);
    }

    /// <summary>
    /// A bare context that maps nothing but the session table, so the assertions see exactly what the
    /// extension configures. Never connects: model building needs no server.
    /// </summary>
    /// <remarks>
    /// The schema is baked into the TYPE rather than passed in, and each schema gets its own type,
    /// because EF caches a built model per context type for the life of the process: one type taking
    /// a schema argument would hand every case whichever model was built first, so the custom-schema
    /// assertion would pass or fail by test order (it did, on CI, where the order differs).
    /// </remarks>
    private abstract class SessionOnlyContextBase(string? schema) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseSqlServer("Server=(local);Database=model-only;Trusted_Connection=True;");

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ApplyRefreshSessionConfiguration(schema);
    }

    private sealed class RefreshSessionOnlyContext() : SessionOnlyContextBase("dbo");

    private sealed class CustomSchemaContext() : SessionOnlyContextBase(CustomSchema);
}
