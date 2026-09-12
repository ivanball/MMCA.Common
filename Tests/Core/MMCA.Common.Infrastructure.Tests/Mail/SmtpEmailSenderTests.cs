using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Mail;

namespace MMCA.Common.Infrastructure.Tests.Mail;

public sealed class SmtpEmailSenderTests
{
    private static SmtpEmailSender CreateSut() =>
        new(Options.Create(new SmtpSettings
        {
            Host = "smtp.test.com",
            Port = 587,
            Username = "user",
            Password = "pass",
            EnableSsl = true,
            From = "from@test.com",
            To = "default@test.com"
        }));

    [Fact]
    public async Task SendAsync_WithNullTo_ThrowsArgumentException()
    {
        var sut = CreateSut();
        var act = () => sut.SendAsync(null!, "subject", "body");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SendAsync_WithEmptyTo_ThrowsArgumentException()
    {
        var sut = CreateSut();
        var act = () => sut.SendAsync(string.Empty, "subject", "body");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SendAsync_WithNullSubject_ThrowsArgumentException()
    {
        var sut = CreateSut();
        var act = () => sut.SendAsync("to@test.com", null!, "body");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SendAsync_WithEmptySubject_ThrowsArgumentException()
    {
        var sut = CreateSut();
        var act = () => sut.SendAsync("to@test.com", string.Empty, "body");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SendAsync_WithNullBody_ThrowsArgumentException()
    {
        var sut = CreateSut();
        var act = () => sut.SendAsync("to@test.com", "subject", null!);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SendAsync_WithEmptyBody_ThrowsArgumentException()
    {
        var sut = CreateSut();
        var act = () => sut.SendAsync("to@test.com", "subject", string.Empty);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── Two-parameter overload (subject, body) ──
    [Fact]
    public async Task SendAsync_TwoParams_WithNullSubject_ThrowsArgumentException()
    {
        var sut = CreateSut();
        var act = () => sut.SendAsync(null!, "body");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SendAsync_TwoParams_WithEmptySubject_ThrowsArgumentException()
    {
        var sut = CreateSut();
        var act = () => sut.SendAsync(string.Empty, "body");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SendAsync_TwoParams_WithNullBody_ThrowsArgumentException()
    {
        var sut = CreateSut();
        var act = () => sut.SendAsync("subject", null!);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SendAsync_TwoParams_WithEmptyBody_ThrowsArgumentException()
    {
        var sut = CreateSut();
        var act = () => sut.SendAsync("subject", string.Empty);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── Bounded send (ADR-070) ──
    // SmtpClient's own default is 100 seconds. A relay that accepts the connection and then stops
    // answering would hold a request thread for the whole of it, one per message in a notification
    // burst, which is longer than any caller in front of it is willing to wait.
    [Fact]
    public void TimeoutSeconds_DefaultsTo30()
        => new SmtpSettings().TimeoutSeconds.Should().Be(30);

    [Fact]
    public void CreateClient_AppliesTheConfiguredTimeoutInMilliseconds()
    {
        using var client = SmtpEmailSender.CreateClient(
            new SmtpSettings { Host = "smtp.test.com", Port = 587, TimeoutSeconds = 45 },
            enableSsl: true);

        client.Timeout.Should().Be(45_000);
    }

    [Fact]
    public void CreateClient_AppliesTheDefaultTimeout_WhenTheHostConfiguresNone()
    {
        using var client = SmtpEmailSender.CreateClient(new SmtpSettings { Host = "smtp.test.com" }, enableSsl: false);

        client.Timeout.Should().Be(30_000, "and never SmtpClient's own 100-second default");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(601)]
    public void TimeoutSeconds_OutOfRange_FailsValidation(int seconds)
        => Validate(new SmtpSettings { TimeoutSeconds = seconds })
            .Should().Contain(r => r.MemberNames.Contains(nameof(SmtpSettings.TimeoutSeconds)));

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(600)]
    public void TimeoutSeconds_InRange_PassesValidation(int seconds)
        => Validate(new SmtpSettings { TimeoutSeconds = seconds })
            .Should().NotContain(r => r.MemberNames.Contains(nameof(SmtpSettings.TimeoutSeconds)));

    /// <summary>
    /// Runs the same data annotations the options pipeline runs at startup
    /// (<c>AddOptions&lt;SmtpSettings&gt;().ValidateDataAnnotations().ValidateOnStart()</c>), so an
    /// out-of-range timeout is a startup failure rather than an instant abort or an unbounded wait.
    /// </summary>
    private static List<ValidationResult> Validate(SmtpSettings settings)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(settings, new ValidationContext(settings), results, validateAllProperties: true);
        return results;
    }
}
