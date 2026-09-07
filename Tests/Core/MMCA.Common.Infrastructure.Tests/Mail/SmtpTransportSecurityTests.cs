using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Infrastructure.Mail;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Mail;

/// <summary>
/// SEC-Common-54: SMTP TLS is secure by default outside Development, resolved in the same three
/// steps as <c>RequireHttpsMetadata</c>. An adopter who configures a hosted relay and omits
/// <c>Smtp:EnableSsl</c> must not authenticate in the clear, because the same path carries the
/// single-use password-reset token.
/// </summary>
public sealed class SmtpTransportSecurityTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static IHostEnvironment Environment(string environmentName)
    {
        var mock = new Mock<IHostEnvironment>();
        mock.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        return mock.Object;
    }

    [Fact]
    public void Resolve_WithNoSettingOutsideDevelopment_EnablesTls()
        => SmtpTransportSecurity.Resolve(Config(), Environment(Environments.Production)).Should().BeTrue();

    [Fact]
    public void Resolve_WithNoSettingInDevelopment_LeavesTlsOff()
        => SmtpTransportSecurity.Resolve(Config(), Environment(Environments.Development)).Should().BeFalse();

    [Fact]
    public void Resolve_WithNoEnvironment_EnablesTls()
        => SmtpTransportSecurity.Resolve(Config(), environment: null).Should().BeTrue();

    [Fact]
    public void Resolve_HonoursAnExplicitFalseOutsideDevelopment()
        => SmtpTransportSecurity
            .Resolve(Config((SmtpTransportSecurity.EnableSslConfigKey, "false")), Environment(Environments.Production))
            .Should().BeFalse();

    [Fact]
    public void Resolve_HonoursAnExplicitTrueInDevelopment()
        => SmtpTransportSecurity
            .Resolve(Config((SmtpTransportSecurity.EnableSslConfigKey, "true")), Environment(Environments.Development))
            .Should().BeTrue();

    [Fact]
    public void Resolve_IgnoresAnUnparseableValueAndStaysSecure()
        => SmtpTransportSecurity
            .Resolve(Config((SmtpTransportSecurity.EnableSslConfigKey, "yes-please")), Environment(Environments.Production))
            .Should().BeTrue();

    [Fact]
    public void Resolve_WarnsWhenADeployedHostDisablesTlsForAConfiguredRelay()
    {
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<Microsoft.Extensions.Logging.LogLevel>())).Returns(true);

        var enabled = SmtpTransportSecurity.Resolve(
            new SmtpSettings { Host = "smtp-relay.example.com" },
            Config((SmtpTransportSecurity.EnableSslConfigKey, "false")),
            Environment(Environments.Production),
            logger.Object);

        enabled.Should().BeFalse();
        logger.Verify(
            l => l.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Resolve_DoesNotWarnInDevelopment()
    {
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<Microsoft.Extensions.Logging.LogLevel>())).Returns(true);

        SmtpTransportSecurity.Resolve(
            new SmtpSettings { Host = "localhost" },
            Config(),
            Environment(Environments.Development),
            logger.Object);

        logger.Verify(
            l => l.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }
}
