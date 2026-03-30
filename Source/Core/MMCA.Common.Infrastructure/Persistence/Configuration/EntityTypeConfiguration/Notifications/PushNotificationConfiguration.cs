using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Notifications.PushNotifications;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration.Notifications;

/// <summary>
/// EF Core configuration for the <see cref="PushNotification"/> entity.
/// Explicitly sets the "Notification" schema because the base class derives schema from the
/// namespace segment before "Domain", which would resolve to "Common" for Common.Domain entities.
/// </summary>
internal sealed class PushNotificationConfiguration(IDataSourceService dataSourceService)
    : EntityTypeConfigurationSQLServer<PushNotification, PushNotificationIdentifierType>(dataSourceService)
{
    /// <inheritdoc />
    public override void Configure(EntityTypeBuilder<PushNotification> builder)
    {
        base.Configure(builder);

        // Override auto-derived schema ("Common") with the correct module schema
        builder.ToTable(nameof(PushNotification), "Notification");

        builder.Property(p => p.Title)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(p => p.Body)
            .IsRequired()
            .HasMaxLength(2000);

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
