using AwesomeAssertions;
using MMCA.Common.Infrastructure.Mail;

namespace MMCA.Common.Infrastructure.Tests.Settings;

public class SmtpSettingsTests
{
    [Fact]
    public void SectionName_IsSmtp() =>
        SmtpSettings.SectionName.Should().Be("Smtp");

    [Fact]
    public void DefaultSmtpPort_Is25() =>
        SmtpSettings.DefaultSmtpPort.Should().Be(25);

    [Theory]
    [InlineData(nameof(SmtpSettings.Host), "")]
    [InlineData(nameof(SmtpSettings.Username), "")]
    [InlineData(nameof(SmtpSettings.Password), "")]
    [InlineData(nameof(SmtpSettings.EnableSsl), false)]
    [InlineData(nameof(SmtpSettings.From), "")]
    [InlineData(nameof(SmtpSettings.To), "")]
    public void Default_PropertyHasTheDocumentedValue(string property, object? expected) =>
        typeof(SmtpSettings).GetProperty(property)!.GetValue(new SmtpSettings()).Should().Be(expected);

    // Kept apart from the theory: the expected value is the static DefaultSmtpPort, not a constant.
    [Fact]
    public void Default_Port_IsDefaultSmtpPort() =>
        new SmtpSettings().Port.Should().Be(SmtpSettings.DefaultSmtpPort);

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
