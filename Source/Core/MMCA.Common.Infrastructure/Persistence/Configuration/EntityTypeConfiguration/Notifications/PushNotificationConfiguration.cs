using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.PushNotifications.Invariants;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration.Notifications;

/// <summary>
/// EF Core configuration for the <see cref="PushNotification"/> entity.
/// Explicitly sets the "Notification" schema (and logical database name via
/// <see cref="UseDatabaseAttribute"/>) because the base class derives both from the namespace
/// segment before "Domain", which would resolve to "Common" for Common.Domain entities.
/// Hosts without a <c>DataSources:Notification</c> entry keep these tables in the default database.
/// </summary>
[UseDatabase("Notification")]
internal sealed class PushNotificationConfiguration
    : EntityTypeConfigurationSQLServer<PushNotification, PushNotificationIdentifierType>
{
    /// <inheritdoc />
    public override void Configure(EntityTypeBuilder<PushNotification> builder)
    {
        base.Configure(builder);

        // Override auto-derived schema ("Common") with the correct module schema
        builder.ToTable(nameof(PushNotification), "Notification");

        builder.Property(p => p.Title)
            .IsRequired()
            .HasMaxLength(PushNotificationInvariants.TitleMaxLength);

        builder.Property(p => p.Body)
            .IsRequired()
            .HasMaxLength(PushNotificationInvariants.BodyMaxLength);

        builder.Property(p => p.SentByUserId)
            .IsRequired();

        builder.Property(p => p.RecipientCount)
            .IsRequired();

        builder.Property(p => p.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(p => p.DedupKey)
            .HasMaxLength(PushNotification.DedupKeyMaxLength);

        // Nullable and deliberately unindexed: the scope filter runs after the primary-key join from
        // UserNotification, over a table that holds one row per send, so an index would cost writes
        // without buying a read.
        builder.Property(p => p.ScopeKey)
            .HasMaxLength(PushNotification.ScopeKeyMaxLength);

        // Filtered unique index: at most one notification per deduplication key, while the many
        // sends that carry no key (NULL) coexist freely. This is what makes a retried send safe,
        // the database arbitrates the race that a check-then-act lookup in the handler cannot.
        // "[DedupKey] IS NOT NULL" is SQL Server filter syntax and matches this configuration's
        // engine base class (EntityTypeConfigurationSQLServer); the Cosmos context strips
        // relational indexes, so no other engine sees it.
        //
        // The IsDeleted clause follows the same precedent SoftDeleteUniqueIndexConvention applies
        // to every other unique index on a soft-deletable entity: without it a soft-deleted row
        // keeps occupying its dedup slot forever and blocks a resend under the same key (BugHunt
        // M58). The convention now appends its clause to a hand-authored filter too, so declaring it
        // here is belt and braces: HasSoftDeleteFilter produces the same SQL, in the same order, from
        // the column name and quoting read off the model, and the convention recognizes it and stops.
        builder.HasIndex(p => p.DedupKey)
            .IsUnique()
            .HasSoftDeleteFilter(additionalFilter: "[DedupKey] IS NOT NULL");
    }
}
