using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Mail;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.Users.UseCases.ForgotPassword;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.ValueObjects.Contact;
using Moq;

namespace MMCA.Common.Application.Tests.Users;

/// <summary>
/// Exercises the anti-enumeration contract of the shared forgot-password workflow: a malformed
/// address, an unknown address, a throttled request and a failed send are all indistinguishable
/// successes, and the email that IS sent carries both the prefilled link and the raw token.
/// </summary>
public sealed class ForgotPasswordHandlerBaseTests
{
    private const string KnownEmail = "user@example.com";
    private const string ResetUrl = "https://app.example.com/reset-password";
    private const string IssuedToken = "issued-token-value";

    [Fact]
    public async Task HandleAsync_WhenEmailMalformed_ReturnsSuccessWithoutLookupIssueOrEmail()
    {
        var (sut, mocks) = CreateSut();

        Result result = await sut.HandleAsync(new TestForgotPasswordCommand(new ForgotPasswordRequest("not-an-email")));

        result.IsSuccess.Should().BeTrue("a malformed address must not be distinguishable from a known one");
        sut.LookupCount.Should().Be(0);
        mocks.TokenService.Verify(
            x => x.IssueAsync(It.IsAny<string>(), It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()),
            Times.Never);
        VerifyNoEmail(mocks);
    }

    [Fact]
    public async Task HandleAsync_WhenEmailUnknown_ReturnsSuccessWithoutIssuingOrEmailing()
    {
        var (sut, mocks) = CreateSut();

        Result result = await sut.HandleAsync(new TestForgotPasswordCommand(new ForgotPasswordRequest(KnownEmail)));

        result.IsSuccess.Should().BeTrue();
        sut.LookupCount.Should().Be(1);
        mocks.TokenService.Verify(
            x => x.IssueAsync(It.IsAny<string>(), It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()),
            Times.Never);
        VerifyNoEmail(mocks);
    }

    [Fact]
    public async Task HandleAsync_WhenEmailKnown_SendsHtmlEmailCarryingBothLinkAndToken()
    {
        var (sut, mocks) = CreateSut();
        sut.Found = new TestIdentityUser { Id = 7 };

        Result result = await sut.HandleAsync(new TestForgotPasswordCommand(new ForgotPasswordRequest("User@Example.com")));

        result.IsSuccess.Should().BeTrue();
        mocks.TokenService.Verify(
            x => x.IssueAsync(KnownEmail, 7, It.IsAny<CancellationToken>()),
            Times.Once,
            "the token is issued against the normalized address and the resolved account");

        mocks.SentTo.Should().Be(KnownEmail);
        mocks.SentAsHtml.Should().BeTrue();
        mocks.SentBody.Should().Contain(IssuedToken, "the raw token must be usable without the link (MAUI head)");
        mocks.SentBody.Should().Contain(ResetUrl);
        mocks.SentBody.Should().Contain("email=user%40example.com");
        mocks.SentBody.Should().Contain($"token={IssuedToken}");
    }

    [Fact]
    public async Task HandleAsync_WhenResetUrlNotConfigured_StillEmailsTheTokenAlone()
    {
        var (sut, mocks) = CreateSut(resetUrl: string.Empty);
        sut.Found = new TestIdentityUser { Id = 7 };

        Result result = await sut.HandleAsync(new TestForgotPasswordCommand(new ForgotPasswordRequest(KnownEmail)));

        result.IsSuccess.Should().BeTrue();
        mocks.SentBody.Should().Contain(IssuedToken);
        mocks.SentBody.Should().NotContain("<a href", "an unconfigured host must not ship a broken link");
    }

    [Fact]
    public async Task HandleAsync_WhenIssueThrottled_ReturnsSuccessWithoutEmailing()
    {
        var (sut, mocks) = CreateSut();
        sut.Found = new TestIdentityUser { Id = 7 };
        mocks.TokenService
            .Setup(x => x.IssueAsync(It.IsAny<string>(), It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<string>(
                Error.Unauthorized("Auth.ResetThrottled", "Too many password reset requests.")));

        Result result = await sut.HandleAsync(new TestForgotPasswordCommand(new ForgotPasswordRequest(KnownEmail)));

        result.IsSuccess.Should().BeTrue("a throttled request must look exactly like an accepted one");
        VerifyNoEmail(mocks);
    }

    [Fact]
    public async Task HandleAsync_WhenSendThrows_StillReturnsSuccess()
    {
        var (sut, mocks) = CreateSut();
        sut.Found = new TestIdentityUser { Id = 7 };
        mocks.EmailSender
            .Setup(x => x.SendAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"));

        Result result = await sut.HandleAsync(new TestForgotPasswordCommand(new ForgotPasswordRequest(KnownEmail)));

        result.IsSuccess.Should().BeTrue("a delivery failure must not become an existence oracle");
    }

    private static void VerifyNoEmail(HandlerMocks mocks) =>
        mocks.EmailSender.Verify(
            x => x.SendAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

    private sealed class HandlerMocks
    {
        public Mock<IUnitOfWork> UnitOfWork { get; } = new();

        public Mock<IPasswordResetTokenService> TokenService { get; } = new();

        public Mock<IEmailSender> EmailSender { get; } = new();

        public string? SentTo { get; set; }

        public string? SentBody { get; set; }

        public bool SentAsHtml { get; set; }
    }

    private static (TestForgotPasswordHandler Sut, HandlerMocks Mocks) CreateSut(string resetUrl = ResetUrl)
    {
        var mocks = new HandlerMocks();

        mocks.TokenService
            .Setup(x => x.IssueAsync(It.IsAny<string>(), It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(IssuedToken));

        mocks.EmailSender
            .Setup(x => x.SendAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback((string to, string subject, string body, bool isHtml, CancellationToken _) =>
            {
                mocks.SentTo = to;
                mocks.SentBody = body;
                mocks.SentAsHtml = isHtml;
            })
            .Returns(Task.CompletedTask);

        var settings = Options.Create(new PasswordResetSettings { ResetUrl = resetUrl });
        var sut = new TestForgotPasswordHandler(
            mocks.UnitOfWork.Object,
            mocks.TokenService.Object,
            mocks.EmailSender.Object,
            settings);

        return (sut, mocks);
    }
}

/// <summary>App-side forgot-password command shape (the shared request payload only).</summary>
public sealed record TestForgotPasswordCommand(ForgotPasswordRequest Request)
    : ICommandWithRequest<ForgotPasswordRequest>;

/// <summary>Concrete subclass standing in for an app's <c>ForgotPasswordHandler</c>.</summary>
public sealed class TestForgotPasswordHandler(
    IUnitOfWork unitOfWork,
    IPasswordResetTokenService tokenService,
    IEmailSender emailSender,
    IOptions<PasswordResetSettings> settings)
    : ForgotPasswordHandlerBase<TestIdentityUser, TestForgotPasswordCommand>(
        unitOfWork, tokenService, emailSender, settings, NullLogger.Instance)
{
    /// <summary>The account the lookup hook resolves, or <see langword="null"/> for an unknown address.</summary>
    public TestIdentityUser? Found { get; set; }

    /// <summary>How many times the app lookup hook ran.</summary>
    public int LookupCount { get; private set; }

    protected override Task<TestIdentityUser?> FindUntrackedByEmailAsync(
        Email email,
        CancellationToken cancellationToken)
    {
        LookupCount++;
        return Task.FromResult(Found);
    }
}
