using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Mail;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Notifications.Push;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.Outbox;

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

    // Asymmetric signing by default: a validator verifies without holding the key that mints tokens,
    // so a host that never sets Jwt:SigningAlgorithm gets the algorithm an extracted service needs.
    [Fact]
    public void Default_SigningAlgorithm_IsRS256() =>
        new JwtSettings().SigningAlgorithm.Should().Be(JwtSigningAlgorithm.RS256);

    [Fact]
    public void Validate_OnTheDefaults_DemandsAnRsaPrivateKey()
    {
        var sut = new JwtSettings { Issuer = "https://issuer.example.com", Audience = "api" };

        sut.Validate(new ValidationContext(sut))
            .Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(JwtSettings.RsaPrivateKeyPem));
    }

    [Fact]
    public void Validate_WithHS256AndAShortSecret_DemandsALongerSecret()
    {
        var sut = new JwtSettings
        {
            SigningAlgorithm = JwtSigningAlgorithm.HS256,
            SecretForKey = "too-short",
            Issuer = "https://issuer.example.com",
            Audience = "api",
        };

        sut.Validate(new ValidationContext(sut))
            .Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(JwtSettings.SecretForKey));
    }

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
    public void Default_PollingIntervalSeconds_Is2() =>
        new OutboxSettings().PollingIntervalSeconds.Should().Be(2);

    [Fact]
    public void Default_ProcessingDelaySeconds_Is5() =>
        new OutboxSettings().ProcessingDelaySeconds.Should().Be(5);

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

// ── MessageBusSettings ──
public class MessageBusSettingsTests
{
    [Fact]
    public void SectionName_IsMessageBus() =>
        MessageBusSettings.SectionName.Should().Be("MessageBus");

    [Fact]
    public void Default_Provider_IsInProcess() =>
        new MessageBusSettings().Provider.Should().Be(MessageBusProvider.InProcess);

    [Fact]
    public void Default_EnableInbox_IsUnset_SoTheTransportDecides() =>
        new MessageBusSettings().EnableInbox.Should().BeNull();

    // Unset resolves from the transport: a broker redelivers by contract, so dedup is ON; in-process
    // dispatch has no redelivery to dedup, so it stays OFF and the InboxMessages table is untouched.
    [Theory]
    [InlineData(MessageBusProvider.InProcess, false)]
    [InlineData(MessageBusProvider.RabbitMq, true)]
    [InlineData(MessageBusProvider.AzureServiceBus, true)]
    public void IsInboxEnabled_Unset_ResolvesFromTheTransport(MessageBusProvider provider, bool expected) =>
        new MessageBusSettings { Provider = provider }.IsInboxEnabled.Should().Be(expected);

    // An explicit value wins in BOTH directions: a host that has not migrated InboxMessages can turn
    // it off under a broker, and a monolith can turn it on.
    [Theory]
    [InlineData(MessageBusProvider.RabbitMq, false)]
    [InlineData(MessageBusProvider.AzureServiceBus, false)]
    [InlineData(MessageBusProvider.InProcess, true)]
    public void IsInboxEnabled_Explicit_OverridesTheTransportDefault(MessageBusProvider provider, bool configured) =>
        new MessageBusSettings { Provider = provider, EnableInbox = configured }
            .IsInboxEnabled.Should().Be(configured);

    [Fact]
    public void Default_EnableOutbox_IsUnset_SoTheTransportDecides() =>
        new MessageBusSettings().EnableOutbox.Should().BeNull();

    // Unset resolves from the transport, same shape as the inbox: a broker deployment publishes
    // exclusively through the outbox, while an in-process host dispatches inside the process that
    // raised the event and pays nothing for store-and-forward it never uses.
    [Theory]
    [InlineData(MessageBusProvider.InProcess, false)]
    [InlineData(MessageBusProvider.RabbitMq, true)]
    [InlineData(MessageBusProvider.AzureServiceBus, true)]
    public void IsOutboxEnabled_Unset_ResolvesFromTheTransport(MessageBusProvider provider, bool expected) =>
        new MessageBusSettings { Provider = provider }.IsOutboxEnabled.Should().Be(expected);

    // A monolith that wants at-least-once delivery across a crash turns it back on. Turning it OFF
    // under a broker parses here and is refused at registration instead, where the failure can name
    // the consequence.
    [Theory]
    [InlineData(MessageBusProvider.InProcess, true)]
    [InlineData(MessageBusProvider.InProcess, false)]
    public void IsOutboxEnabled_Explicit_OverridesTheTransportDefault(MessageBusProvider provider, bool configured) =>
        new MessageBusSettings { Provider = provider, EnableOutbox = configured }
            .IsOutboxEnabled.Should().Be(configured);

    // Default-off is load-bearing, not incidental: RabbitMQ needs the delayed-message-exchange
    // plugin, which the Aspire development container does not ship, so a default-on flag would
    // fail bus start on every local run.
    [Fact]
    public void Default_EnableDelayedRedelivery_IsFalse() =>
        new MessageBusSettings().EnableDelayedRedelivery.Should().BeFalse();

    [Fact]
    public void Default_RedeliveryIntervalsSeconds_IsOneMinuteTenMinutesOneHour() =>
        new MessageBusSettings().RedeliveryIntervalsSeconds.Should().Equal(60, 600, 3600);

    [Fact]
    public void ResilienceProperties_RoundTrip()
    {
        var sut = new MessageBusSettings
        {
            EnableInbox = true,
            EnableDelayedRedelivery = true,
            RedeliveryIntervalsSeconds = [5, 15],
        };

        sut.EnableInbox.Should().BeTrue();
        sut.EnableDelayedRedelivery.Should().BeTrue();
        sut.RedeliveryIntervalsSeconds.Should().Equal(5, 15);
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
    public void Default_ChannelKeyPattern_MatchesEventAndSessionKeys() =>
        new PushNotificationSettings().ChannelKeyPattern.Should().Be("^(event|session):[0-9]+$");

    [Fact]
    public void Properties_RoundTrip()
    {
        var sut = new PushNotificationSettings
        {
            Enabled = true,
            HubPath = "/custom/hub",
            ChannelKeyPattern = "^room:[a-z]+$",
        };

        sut.Enabled.Should().BeTrue();
        sut.HubPath.Should().Be("/custom/hub");
        sut.ChannelKeyPattern.Should().Be("^room:[a-z]+$");
    }

}
