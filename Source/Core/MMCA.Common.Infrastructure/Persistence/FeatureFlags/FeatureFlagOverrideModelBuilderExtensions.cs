using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.FeatureFlags;

namespace MMCA.Common.Infrastructure.Persistence.FeatureFlags;

/// <summary>
/// Maps the <see cref="FeatureFlagOverride"/> table into a model (ADR-031).
/// <para>
/// <b>Opt-in, like the stored permission grants.</b> <c>AddReloadableFeatureFlags</c> registers the
/// model gate <c>ApplicationDbContext</c> reads, and the context calls this for the one source named
/// by <c>FeatureFlags:DataSourceName</c>, so no consumer calls it by hand and every other database's
/// model stays byte-identical. A host with its own context class can still call it directly.
/// </para>
/// </summary>
public static class FeatureFlagOverrideModelBuilderExtensions
{
    /// <summary>The table name the configuration maps to.</summary>
    public const string TableName = "FeatureFlagOverrides";

    /// <summary>The column the flag name (the entity's key) is stored in.</summary>
    public const string NameColumn = "Name";

    /// <summary>Column width of the stored mode name.</summary>
    private const int ModeMaxLength = 16;

    /// <summary>
    /// Configures <see cref="FeatureFlagOverride"/> on the given model. Call it BEFORE the soft-delete,
    /// tenant and concurrency-token passes run, so the override is treated like any other auditable
    /// entity (<c>ApplicationDbContext</c> does).
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="schema">Table schema; defaults to <c>dbo</c>, matching the framework's own tables.</param>
    /// <returns>The same model builder, for chaining.</returns>
    public static ModelBuilder ApplyFeatureFlagOverrideConfiguration(this ModelBuilder modelBuilder, string? schema = "dbo")
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<FeatureFlagOverride>(entity =>
        {
            entity.ToTable(TableName, schema);
            entity.HasKey(e => e.Id);

            // Non-unicode: flag names are ASCII identifiers by the *Features convention.
            entity.Property(e => e.Id)
                  .HasColumnName(NameColumn)
                  .HasMaxLength(FeatureFlagOverride.NameMaxLength)
                  .IsUnicode(false)
                  .ValueGeneratedNever();

            // Stored by name so a reordered enum can never silently change what a row means.
            entity.Property(e => e.Mode)
                  .HasConversion<string>()
                  .HasMaxLength(ModeMaxLength)
                  .IsUnicode(false);

            entity.Property(e => e.TargetUsers).HasMaxLength(FeatureFlagOverride.TargetListMaxLength);
            entity.Property(e => e.TargetGroups).HasMaxLength(FeatureFlagOverride.TargetListMaxLength);
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(FeatureFlagOverride.ReasonMaxLength);

            entity.Ignore(e => e.Name);
            entity.Ignore(e => e.Users);
            entity.Ignore(e => e.Groups);
        });

        return modelBuilder;
    }
}
