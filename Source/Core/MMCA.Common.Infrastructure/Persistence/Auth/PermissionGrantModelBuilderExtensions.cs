using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Auth;

namespace MMCA.Common.Infrastructure.Persistence.Auth;

/// <summary>
/// Maps the <see cref="PermissionGrant"/> table into a consumer's model.
/// <para>
/// <b>Opt-in, exactly like the refresh sessions.</b> Grants are Identity-module data: one database
/// owns them, and mapping them everywhere would put an empty <c>PermissionGrants</c> table in the
/// migrations of every other module's database. The consumer's Identity context calls this from its
/// own <c>OnModelCreating</c>.
/// </para>
/// </summary>
public static class PermissionGrantModelBuilderExtensions
{
    /// <summary>The table name the configuration maps to.</summary>
    public const string TableName = "PermissionGrants";

    /// <summary>The unique index over role plus permission, which is what makes a grant idempotent.</summary>
    public const string RolePermissionIndexName = "IX_PermissionGrants_Role_Permission";

    /// <summary>
    /// Configures <see cref="PermissionGrant"/> on the given model. Call it from the Identity
    /// database's context (after <c>base.OnModelCreating(modelBuilder)</c>).
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="schema">Table schema; defaults to <c>dbo</c>, matching the framework's own tables.</param>
    /// <returns>The same model builder, for chaining.</returns>
    public static ModelBuilder ApplyPermissionGrantConfiguration(this ModelBuilder modelBuilder, string? schema = "dbo")
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<PermissionGrant>(entity =>
        {
            entity.ToTable(TableName, schema);
            entity.HasKey(e => e.Id);

            // Non-unicode: role and permission names are ASCII identifiers by convention, so a unicode
            // column would double the index width for nothing.
            entity.Property(e => e.Role)
                  .IsRequired()
                  .HasMaxLength(PermissionGrant.RoleMaxLength)
                  .IsUnicode(false);

            entity.Property(e => e.Permission)
                  .IsRequired()
                  .HasMaxLength(PermissionGrant.PermissionMaxLength)
                  .IsUnicode(false);

            entity.Property(e => e.GrantedBy).HasMaxLength(256);

            // Unique because a grant is a set membership, not a log: two rows saying the same role
            // grants the same permission mean nothing extra, and letting them exist would turn
            // "revoke" into a question of how many rows to delete. It is also what lets the store's
            // idempotent grant rely on the database rather than on a read-then-write race.
            entity.HasIndex(e => new { e.Role, e.Permission })
                  .IsUnique()
                  .HasDatabaseName(RolePermissionIndexName);
        });

        return modelBuilder;
    }
}
