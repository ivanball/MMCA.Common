using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration.Notifications;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Design;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Configuration;

/// <summary>
/// The notification configurations are declared for SQL Server, but a host that configures only
/// PostgreSQL substitutes its engine and applies them to the PostgreSQL model (M117). Their index
/// filters must then be PostgreSQL SQL (<c>"IsDeleted" = false</c>), not the bracketed SQL Server
/// literal, which is a syntax error on Npgsql. The SQL Server model keeps its bracketed form.
/// </summary>
/// <remarks>
/// Each case builds its context over its own source name: EF caches a built model per (context type,
/// source name) for the life of the process.
/// </remarks>
public sealed class NotificationConfigurationEngineTests
{
    private const string NotificationSource = "Notification";

    [Fact]
    public void OnAPostgreSqlOnlyHost_NotificationIndexFilters_UsePostgreSqlSyntax()
    {
        using var context = DesignTimeDbContextHelper.CreatePostgreSQL(
            ["--datasource", NotificationSource],
            options =>
            {
                options.ConnectionStrings = new ConnectionStringSettings
                {
                    PostgreSQLConnectionString = "Host=design;Database=main;Username=app",
                };
                options.DataSources[NotificationSource] = new DataSourceEntrySettings
                {
                    PostgreSQLConnectionString = "Host=design;Database=notification-engine;Username=app",
                };
                options.AddConfigurationAssembly(typeof(UserNotificationConfiguration).Assembly);
            });

        UserNotificationIndexFilters(context).Should().AllBe("\"IsDeleted\" = false");
        DedupKeyIndex(context).GetFilter().Should().Be("\"DedupKey\" IS NOT NULL AND \"IsDeleted\" = false");
    }

    [Fact]
    public void OnASqlServerHost_NotificationIndexFilters_KeepTheBracketedForm()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", NotificationSource],
            options =>
            {
                options.ConnectionStrings = new ConnectionStringSettings
                {
                    SQLServerConnectionString = "Server=design;Database=Main;",
                };
                options.DataSources[NotificationSource] = new DataSourceEntrySettings
                {
                    SQLServerConnectionString = "Server=design;Database=NotificationEngine;",
                };
                options.AddConfigurationAssembly(typeof(UserNotificationConfiguration).Assembly);
            });

        UserNotificationIndexFilters(context).Should().AllBe("[IsDeleted] = 0");
        DedupKeyIndex(context).GetFilter().Should().Be("[DedupKey] IS NOT NULL AND [IsDeleted] = 0");
    }

    private static IEnumerable<string?> UserNotificationIndexFilters(ApplicationDbContext context)
    {
        var indexes = context.Model.FindEntityType(typeof(UserNotification))!.GetIndexes().ToList();
        indexes.Should().HaveCount(2);
        return indexes.Select(i => i.GetFilter());
    }

    private static IIndex DedupKeyIndex(ApplicationDbContext context) =>
        context.Model.FindEntityType(typeof(PushNotification))!
            .GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(PushNotification.DedupKey));
}
