using AwesomeAssertions;
using MMCA.Common.Infrastructure.Services;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class SmtpEmailSenderTests
{
    private static SmtpEmailSender CreateSut() =>
        new(new SmtpSettings
        {
            Host = "smtp.test.com",
            Port = 587,
            Username = "user",
            Password = "pass",
            EnableSsl = true,
            From = "from@test.com",
            To = "default@test.com"
        });

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
}
