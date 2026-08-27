using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Auth;

namespace MMCA.Common.Infrastructure.Persistence.Auth;

/// <summary>
/// Maps the <see cref="RefreshSession"/> table into a consumer's model.
/// <para>
/// <b>Opt-in, unlike the outbox.</b> The outbox is cross-cutting infrastructure and is configured on
/// <c>ApplicationDbContext</c> itself, so every relational source gets the table. Refresh sessions are
/// Identity-module data: exactly one database owns them, and mapping them everywhere would put an
/// empty <c>RefreshSessions</c> table in the migrations of every other module's database. The
/// consumer's Identity context calls this from its own <c>OnModelCreating</c> instead.
/// </para>
/// </summary>
public static class RefreshSessionModelBuilderExtensions
{
    /// <summary>The table name the configuration maps to.</summary>
    public const string TableName = "RefreshSessions";

    /// <summary>The unique index over the token hash, the reuse-detection lookup path.</summary>
    public const string TokenHashIndexName = "IX_RefreshSessions_TokenHash";

    /// <summary>The per-user index backing the family revocation and the session cap.</summary>
    public const string UserIndexName = "IX_RefreshSessions_UserId";

    /// <summary>
    /// Configures <see cref="RefreshSession"/> on the given model. Call it from the Identity
    /// database's context (after <c>base.OnModelCreating(modelBuilder)</c>).
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="schema">Table schema; defaults to <c>dbo</c>, matching the framework's own tables.</param>
    /// <returns>The same model builder, for chaining.</returns>
    public static ModelBuilder ApplyRefreshSessionConfiguration(this ModelBuilder modelBuilder, string? schema = "dbo")
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<RefreshSession>(entity =>
        {
            entity.ToTable(TableName, schema);
            entity.HasKey(e => e.Id);

            // Fixed-width non-unicode: the value is always a 64-character hex digest, so a unicode
            // or variable-width column would double the index it has to fit in for nothing.
            entity.Property(e => e.TokenHash)
                  .IsRequired()
                  .HasMaxLength(RefreshSession.TokenHashLength)
                  .IsUnicode(false)
                  .IsFixedLength();

            entity.Property(e => e.ReplacedByTokenHash)
                  .HasMaxLength(RefreshSession.TokenHashLength)
                  .IsUnicode(false)
                  .IsFixedLength();

            entity.Property(e => e.ReasonRevoked).HasMaxLength(RefreshSession.ReasonRevokedMaxLength).IsUnicode(false);
            entity.Property(e => e.IpAddress).HasMaxLength(RefreshSession.IpAddressMaxLength).IsUnicode(false);
            entity.Property(e => e.UserAgent).HasMaxLength(RefreshSession.UserAgentMaxLength);

            // Validation path: every refresh presents a token and is answered by exactly one row.
            // Unique because a hash collision across users would let one account's token be validated
            // against another's session, and because it makes a double-insert of the same token a
            // database error rather than an ambiguity the reuse check has to resolve.
            entity.HasIndex(e => e.TokenHash)
                  .IsUnique()
                  .HasDatabaseName(TokenHashIndexName);

            // Family path: "every live session for this user", asked on the cap check, on reuse
            // detection and on sign-out-everywhere. Without it each of those scans the table.
            entity.HasIndex(e => new { e.UserId, e.RevokedAt })
                  .HasDatabaseName(UserIndexName);
        });

        return modelBuilder;
    }
}
