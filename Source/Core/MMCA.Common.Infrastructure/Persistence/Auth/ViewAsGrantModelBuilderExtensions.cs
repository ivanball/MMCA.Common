using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Auth;

namespace MMCA.Common.Infrastructure.Persistence.Auth;

/// <summary>
/// Maps the <see cref="ViewAsGrant"/> table into a consumer's model (ADR-137). Applied by
/// <c>ApplicationDbContext</c> to the one context whose source is named by
/// <c>ViewAs:DataSourceName</c>, and only while <c>ViewAs:Enabled</c> is true, so a host that never
/// opts in keeps a byte-identical model (the refresh-session precedent).
/// </summary>
public static class ViewAsGrantModelBuilderExtensions
{
    /// <summary>The table name the configuration maps to.</summary>
    public const string TableName = "ViewAsGrants";

    /// <summary>The <c>(TargetUserId, GrantedAt)</c> index backing the viewed account's history.</summary>
    public const string TargetIndexName = "IX_ViewAsGrants_TargetUserId_GrantedAt";

    /// <summary>The <c>GrantedAt</c> index backing the retention sweep.</summary>
    public const string GrantedAtIndexName = "IX_ViewAsGrants_GrantedAt";

    /// <summary>Configures <see cref="ViewAsGrant"/> on the given model.</summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="schema">Table schema; defaults to <c>dbo</c>, matching the framework's own tables.</param>
    /// <returns>The same model builder, for chaining.</returns>
    public static ModelBuilder ApplyViewAsGrantConfiguration(this ModelBuilder modelBuilder, string? schema = "dbo")
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ViewAsGrant>(entity =>
        {
            entity.ToTable(TableName, schema);
            entity.HasKey(e => e.Id);

            // The primary key IS the per-request lookup (the token's vag claim), so the guard's check
            // is one seek. No other columns are read on that path.
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(ViewAsGrant.ReasonMaxLength);
            entity.Property(e => e.EndedAt);

            // History path: the viewed account's grants, newest first.
            entity.HasIndex(e => new { e.TargetUserId, e.GrantedAt })
                  .HasDatabaseName(TargetIndexName);

            // Retention path: rows issued before the cutoff.
            entity.HasIndex(e => e.GrantedAt)
                  .HasDatabaseName(GrantedAtIndexName);
        });

        return modelBuilder;
    }
}
