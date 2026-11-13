using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Webhooks;

namespace MMCA.Common.Infrastructure.Persistence.Webhooks;

/// <summary>
/// Maps the <see cref="WebhookSubscription"/> table into a consumer's model. Applied by
/// <c>ApplicationDbContext</c> to the one context whose source is named by
/// <c>Webhooks:DataSourceName</c>, and only while <c>Webhooks:Enabled</c> is true, so a host that never
/// opts in keeps a byte-identical model (the machine-client precedent).
/// </summary>
public static class WebhookSubscriptionModelBuilderExtensions
{
    /// <summary>The table name the configuration maps to.</summary>
    public const string TableName = "WebhookSubscriptions";

    /// <summary>The index every tenant-filtered read uses.</summary>
    public const string TenantIndexName = "IX_WebhookSubscriptions_TenantId_IsDeleted";

    /// <summary>
    /// Configures <see cref="WebhookSubscription"/> on the given model. Call it BEFORE the soft-delete,
    /// tenant and concurrency-token passes run, so the subscription is treated like any other auditable
    /// entity (<c>ApplicationDbContext</c> does).
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="schema">Table schema; defaults to <c>dbo</c>, matching the framework's own tables.</param>
    /// <returns>The same model builder, for chaining.</returns>
    public static ModelBuilder ApplyWebhookSubscriptionConfiguration(this ModelBuilder modelBuilder, string? schema = "dbo")
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<WebhookSubscription>(entity =>
        {
            entity.ToTable(TableName, schema);
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.Url).IsRequired().HasMaxLength(WebhookSubscription.UrlMaxLength);
            entity.Property(e => e.Description).HasMaxLength(WebhookSubscription.DescriptionMaxLength);
            entity.Property(e => e.EventTypes).IsRequired().IsUnicode(false).HasMaxLength(WebhookSubscription.EventTypesMaxLength);
            entity.Property(e => e.TenantId).IsUnicode(false).HasMaxLength(WebhookSubscription.TenantIdMaxLength);
            entity.Property(e => e.ProtectedSecret).IsRequired().HasMaxLength(WebhookSubscription.ProtectedSecretMaxLength);
            entity.Property(e => e.PreviousProtectedSecret).HasMaxLength(WebhookSubscription.ProtectedSecretMaxLength);
            entity.Property(e => e.PreviousSecretExpiresOn);
            entity.Property(e => e.DisabledOn);
            entity.Property(e => e.LastDeliveredOn);
            entity.Property(e => e.LastFailedOn);
            entity.Property(e => e.LastFailure).HasMaxLength(WebhookSubscription.LastFailureMaxLength);

            // Every read filters by tenant (null matching only null) and excludes soft-deleted rows.
            entity.HasIndex(e => new { e.TenantId, e.IsDeleted }).HasDatabaseName(TenantIndexName);
        });

        return modelBuilder;
    }
}
