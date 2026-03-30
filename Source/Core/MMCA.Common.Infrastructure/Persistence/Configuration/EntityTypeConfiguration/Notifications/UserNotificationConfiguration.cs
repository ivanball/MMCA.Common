using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Notifications.UserNotifications;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration.Notifications;

/// <summary>
/// EF Core configuration for the <see cref="UserNotification"/> entity.
/// Explicitly sets the "Notification" schema because the base class derives schema from the
/// namespace segment before "Domain", which would resolve to "Common" for Common.Domain entities.
/// </summary>
internal sealed class UserNotificationConfiguration(IDataSourceService dataSourceService)
    : EntityTypeConfigurationSQLServer<UserNotification, UserNotificationIdentifierType>(dataSourceService)
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
