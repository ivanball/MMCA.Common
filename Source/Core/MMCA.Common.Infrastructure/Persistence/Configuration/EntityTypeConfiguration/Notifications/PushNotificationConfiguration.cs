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
    }
}
