using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Auth.MachineClients;

namespace MMCA.Common.Infrastructure.Persistence.MachineClients;

/// <summary>
/// Maps the <see cref="MachineClient"/> table into a consumer's model (ADR-140). Applied by
/// <c>ApplicationDbContext</c> to the one context whose source is named by
/// <c>MachineClients:DataSourceName</c>, and only while <c>MachineClients:Enabled</c> is true, so a host
/// that never opts in keeps a byte-identical model (the passkey precedent).
/// </summary>
public static class MachineClientModelBuilderExtensions
{
    /// <summary>The table name the configuration maps to.</summary>
    public const string TableName = "MachineClients";

    /// <summary>The unique index on the service account (one client per service account).</summary>
    public const string ServiceAccountIndexName = "IX_MachineClients_ServiceAccountUserId";

    /// <summary>
    /// Configures <see cref="MachineClient"/> on the given model. Call it BEFORE the soft-delete,
    /// tenant and concurrency-token passes run, so the client is treated like any other auditable
    /// entity (<c>ApplicationDbContext</c> does).
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="schema">Table schema; defaults to <c>dbo</c>, matching the framework's own tables.</param>
    /// <returns>The same model builder, for chaining.</returns>
    public static ModelBuilder ApplyMachineClientConfiguration(this ModelBuilder modelBuilder, string? schema = "dbo")
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<MachineClient>(entity =>
        {
            entity.ToTable(TableName, schema);
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.Name).IsRequired().HasMaxLength(MachineClient.NameMaxLength);
            entity.Property(e => e.PrimarySecretHash).IsRequired().HasMaxLength(MachineClientSecret.HashByteLength);
            entity.Property(e => e.SecondarySecretHash).HasMaxLength(MachineClientSecret.HashByteLength);
            entity.Property(e => e.SecretExpiresOn);
            entity.Property(e => e.RevokedOn);
            entity.Property(e => e.LastUsedOn);

            entity.Ignore(e => e.IsRevoked);

            // One client per service account: the account's role is the client's whole authority.
            entity.HasIndex(e => e.ServiceAccountUserId).IsUnique().HasDatabaseName(ServiceAccountIndexName);
        });

        return modelBuilder;
    }
}
