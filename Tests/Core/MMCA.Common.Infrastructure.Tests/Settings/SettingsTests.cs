using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Tests.Settings;

// ── JwtSettings ──
public class JwtSettingsTests
{
    [Fact]
    public void SectionName_IsJwt() =>
        JwtSettings.SectionName.Should().Be("Jwt");

    [Fact]
    public void Default_SecretForKey_IsEmpty() =>
        new JwtSettings().SecretForKey.Should().BeEmpty();

    [Fact]
    public void Default_Issuer_IsEmpty() =>
        new JwtSettings().Issuer.Should().BeEmpty();

    [Fact]
    public void Default_Audience_IsEmpty() =>
        new JwtSettings().Audience.Should().BeEmpty();

    [Fact]
    public void Default_AccessTokenExpirationMinutes_Is15() =>
        new JwtSettings().AccessTokenExpirationMinutes.Should().Be(15);

    [Fact]
    public void Default_RefreshTokenExpirationDays_Is7() =>
        new JwtSettings().RefreshTokenExpirationDays.Should().Be(7);

    [Fact]
    public void Properties_RoundTrip()
    {
        var sut = new JwtSettings
        {
            SecretForKey = "super-secret-key-that-is-32-chars!",
            Issuer = "https://issuer.example.com",
            Audience = "https://audience.example.com",
            AccessTokenExpirationMinutes = 60,
            RefreshTokenExpirationDays = 14,
        };

        sut.SecretForKey.Should().Be("super-secret-key-that-is-32-chars!");
        sut.Issuer.Should().Be("https://issuer.example.com");
        sut.Audience.Should().Be("https://audience.example.com");
        sut.AccessTokenExpirationMinutes.Should().Be(60);
        sut.RefreshTokenExpirationDays.Should().Be(14);
    }

    [Fact]
    public void Implements_IJwtSettings()
    {
        IJwtSettings sut = new JwtSettings();
        sut.Should().BeAssignableTo<IJwtSettings>();
    }
}

// ── SmtpSettings ──
public class SmtpSettingsTests
{
    [Fact]
    public void SectionName_IsSmtp() =>
        SmtpSettings.SectionName.Should().Be("Smtp");

    [Fact]
    public void DefaultSmtpPort_Is25() =>
        SmtpSettings.DefaultSmtpPort.Should().Be(25);

    [Fact]
    public void Default_Host_IsEmpty() =>
        new SmtpSettings().Host.Should().BeEmpty();

    [Fact]
    public void Default_Port_IsDefaultSmtpPort() =>
        new SmtpSettings().Port.Should().Be(SmtpSettings.DefaultSmtpPort);

    [Fact]
    public void Default_Username_IsEmpty() =>
        new SmtpSettings().Username.Should().BeEmpty();

    [Fact]
    public void Default_Password_IsEmpty() =>
        new SmtpSettings().Password.Should().BeEmpty();

    [Fact]
    public void Default_EnableSsl_IsFalse() =>
        new SmtpSettings().EnableSsl.Should().BeFalse();

    [Fact]
    public void Default_From_IsEmpty() =>
        new SmtpSettings().From.Should().BeEmpty();

    [Fact]
    public void Default_To_IsEmpty() =>
        new SmtpSettings().To.Should().BeEmpty();

    [Fact]
    public void Properties_RoundTrip()
    {
        var sut = new SmtpSettings
        {
            Host = "smtp.example.com",
            Port = 587,
            Username = "user@example.com",
            Password = "s3cret",
            EnableSsl = true,
            From = "noreply@example.com",
            To = "admin@example.com",
        };

        sut.Host.Should().Be("smtp.example.com");
        sut.Port.Should().Be(587);
        sut.Username.Should().Be("user@example.com");
        sut.Password.Should().Be("s3cret");
        sut.EnableSsl.Should().BeTrue();
        sut.From.Should().Be("noreply@example.com");
        sut.To.Should().Be("admin@example.com");
    }

    [Fact]
    public void Implements_ISmtpSettings()
    {
        ISmtpSettings sut = new SmtpSettings();
        sut.Should().BeAssignableTo<ISmtpSettings>();
    }
}

// ── ConnectionStringSettings ──
public class ConnectionStringSettingsTests
{
    [Fact]
    public void SectionName_IsConnectionStrings() =>
        ConnectionStringSettings.SectionName.Should().Be("ConnectionStrings");

    [Fact]
    public void Default_CosmosConnectionString_IsEmpty() =>
        new ConnectionStringSettings().CosmosConnectionString.Should().BeEmpty();

    [Fact]
    public void Default_SqliteConnectionString_IsEmpty() =>
        new ConnectionStringSettings().SqliteConnectionString.Should().BeEmpty();

    [Fact]
    public void Default_SQLServerConnectionString_IsEmpty() =>
        new ConnectionStringSettings().SQLServerConnectionString.Should().BeEmpty();

    [Fact]
    public void Default_SQLServerMigrationsAssembly_IsEmpty() =>
        new ConnectionStringSettings().SQLServerMigrationsAssembly.Should().BeEmpty();

    [Fact]
    public void Properties_RoundTrip()
    {
        var sut = new ConnectionStringSettings
        {
            CosmosConnectionString = "AccountEndpoint=https://cosmos.example.com;AccountKey=key",
            SqliteConnectionString = "Data Source=app.db",
            SQLServerConnectionString = "Server=.;Database=AppDb;Trusted_Connection=True",
            SQLServerMigrationsAssembly = "MyApp.Migrations",
        };

        sut.CosmosConnectionString.Should().Be("AccountEndpoint=https://cosmos.example.com;AccountKey=key");
        sut.SqliteConnectionString.Should().Be("Data Source=app.db");
        sut.SQLServerConnectionString.Should().Be("Server=.;Database=AppDb;Trusted_Connection=True");
        sut.SQLServerMigrationsAssembly.Should().Be("MyApp.Migrations");
    }

    [Fact]
    public void Implements_IConnectionStringSettings()
    {
        IConnectionStringSettings sut = new ConnectionStringSettings();
        sut.Should().BeAssignableTo<IConnectionStringSettings>();
    }
}

// ── OutboxSettings ──
public class OutboxSettingsTests
{
    [Fact]
    public void SectionName_IsOutbox() =>
        OutboxSettings.SectionName.Should().Be("Outbox");

    [Fact]
    public void Default_BatchSize_Is50() =>
        new OutboxSettings().BatchSize.Should().Be(50);

    [Fact]
    public void Default_MaxRetries_Is5() =>
        new OutboxSettings().MaxRetries.Should().Be(5);

    [Fact]
    public void Default_PollingIntervalSeconds_Is10() =>
        new OutboxSettings().PollingIntervalSeconds.Should().Be(10);

    [Fact]
    public void Default_ProcessingDelaySeconds_Is30() =>
        new OutboxSettings().ProcessingDelaySeconds.Should().Be(30);

    [Fact]
    public void Default_DataSource_IsSQLServer() =>
        new OutboxSettings().DataSource.Should().Be(DataSource.SQLServer);

    [Fact]
    public void Properties_RoundTrip()
    {
        var sut = new OutboxSettings
        {
            BatchSize = 100,
            MaxRetries = 10,
            PollingIntervalSeconds = 30,
            ProcessingDelaySeconds = 60,
            DataSource = DataSource.Sqlite,
        };

        sut.BatchSize.Should().Be(100);
        sut.MaxRetries.Should().Be(10);
        sut.PollingIntervalSeconds.Should().Be(30);
        sut.ProcessingDelaySeconds.Should().Be(60);
        sut.DataSource.Should().Be(DataSource.Sqlite);
    }
}

// ── PushNotificationSettings ──
public class PushNotificationSettingsTests
{
    [Fact]
    public void SectionName_IsPushNotifications() =>
        PushNotificationSettings.SectionName.Should().Be("PushNotifications");

    [Fact]
    public void Default_Enabled_IsFalse() =>
        new PushNotificationSettings().Enabled.Should().BeFalse();

    [Fact]
    public void Default_HubPath_IsHubsNotifications() =>
        new PushNotificationSettings().HubPath.Should().Be("/hubs/notifications");

    [Fact]
    public void Properties_RoundTrip()
    {
        var sut = new PushNotificationSettings
        {
            Enabled = true,
            HubPath = "/custom/hub",
        };

        sut.Enabled.Should().BeTrue();
        sut.HubPath.Should().Be("/custom/hub");
    }

    [Fact]
    public void Implements_IPushNotificationSettings()
    {
        IPushNotificationSettings sut = new PushNotificationSettings();
        sut.Should().BeAssignableTo<IPushNotificationSettings>();
    }
}
