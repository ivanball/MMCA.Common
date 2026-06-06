using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Domain.Notifications.UserNotifications;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration.Notifications;

/// <summary>
/// EF Core configuration for the <see cref="UserNotification"/> entity.
/// Explicitly sets the "Notification" schema (and logical database name via
/// <see cref="UseDatabaseAttribute"/>) because the base class derives both from the namespace
/// segment before "Domain", which would resolve to "Common" for Common.Domain entities.
/// Hosts without a <c>DataSources:Notification</c> entry keep these tables in the default database.
/// </summary>
[UseDatabase("Notification")]
internal sealed class UserNotificationConfiguration
    : EntityTypeConfigurationSQLServer<UserNotification, UserNotificationIdentifierType>
{
    /// <inheritdoc />
    public override void Configure(EntityTypeBuilder<UserNotification> builder)
    {
        base.Configure(builder);

        // Override auto-derived schema ("Common") with the correct module schema
        builder.ToTable(nameof(UserNotification), "Notification");

        builder.Property(p => p.UserId)
            .IsRequired();

        builder.Property(p => p.PushNotificationId)
            .IsRequired();

        builder.Property(p => p.IsRead)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(p => p.ReadOn);

        // Unique: one inbox entry per user per notification (among non-deleted)
        builder.HasIndex(p => new { p.UserId, p.PushNotificationId })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0");

        // Fast lookup for user's unread notifications
        builder.HasIndex(p => new { p.UserId, p.IsRead })
            .HasFilter("[IsDeleted] = 0");
    }
}
