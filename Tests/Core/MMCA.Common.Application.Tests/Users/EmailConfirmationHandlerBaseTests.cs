using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.EmailConfirmation;
using MMCA.Common.Application.Interfaces.Infrastructure.Mail;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.Users.UseCases.EmailConfirmation;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.ValueObjects.Contact;
using Moq;

namespace MMCA.Common.Application.Tests.Users;

/// <summary>
/// Exercises the two shared email-confirmation use-case bases: the send side is an anti-enumeration
/// success whatever happens, and the confirm side redeems a token exactly once, collapses every
/// rejection to one error, and treats an already-confirmed address as a duplicate rather than a fault.
/// </summary>
public sealed class EmailConfirmationHandlerBaseTests
{
    private const string KnownEmail = "user@example.com";
    private const string ConfirmationUrl = "https://app.example.com/confirm-email";
    private const string IssuedToken = "issued-confirmation-token";
    private const UserIdentifierType TestUserId = 21;

    // ── Send ──
    [Fact]
    public async Task Send_WhenTheAddressIsMalformed_SucceedsWithoutLookupOrEmail()
    {
        var (sut, mocks) = CreateSendSut();

        Result result = await sut.HandleAsync(new TestSendConfirmationCommand(new SendEmailConfirmationRequest("nope")));

        result.IsSuccess.Should().BeTrue("a malformed address must not be distinguishable from a known one");
        sut.LookupCount.Should().Be(0);
        VerifyNoEmail(mocks);
    }

    [Fact]
    public async Task Send_WhenTheAddressIsUnknown_SucceedsWithoutIssuingOrEmailing()
    {
        var (sut, mocks) = CreateSendSut();

        Result result = await sut.HandleAsync(new TestSendConfirmationCommand(new SendEmailConfirmationRequest(KnownEmail)));

        result.IsSuccess.Should().BeTrue();
        mocks.TokenService.Verify(
            x => x.IssueAsync(It.IsAny<string>(), It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()),
            Times.Never);
        VerifyNoEmail(mocks);
    }

    [Fact]
    public async Task Send_WhenTheAddressIsAlreadyConfirmed_SucceedsWithoutIssuingAnotherToken()
    {
        // A live token for a confirmed address is of no use to the owner and is one more redeemable
        // secret in flight.
        var (sut, mocks) = CreateSendSut();
        sut.Found = new ConfirmableUser { Id = TestUserId, Confirmed = true };

        Result result = await sut.HandleAsync(new TestSendConfirmationCommand(new SendEmailConfirmationRequest(KnownEmail)));

        result.IsSuccess.Should().BeTrue();
        mocks.TokenService.Verify(
            x => x.IssueAsync(It.IsAny<string>(), It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()),
            Times.Never);
        VerifyNoEmail(mocks);
    }

    [Fact]
    public async Task Send_ForAnUnconfirmedAccount_EmailsBothTheLinkAndTheRawToken()
    {
        var (sut, mocks) = CreateSendSut();
        sut.Found = new ConfirmableUser { Id = TestUserId, Confirmed = false };

        Result result = await sut.HandleAsync(new TestSendConfirmationCommand(new SendEmailConfirmationRequest(KnownEmail)));

        result.IsSuccess.Should().BeTrue();
        mocks.SentTo.Should().Be(KnownEmail);
        mocks.SentAsHtml.Should().BeTrue();
        mocks.SentBody.Should().Contain(IssuedToken, "a client with no deep linking needs the token typed in by hand");
        mocks.SentBody.Should().Contain(ConfirmationUrl + "#email=", "the token rides the fragment, never the query string");
    }

    [Fact]
    public async Task Send_WithNoConfirmationUrlConfigured_StillEmailsTheToken()
    {
        var (sut, mocks) = CreateSendSut(confirmationUrl: string.Empty);
        sut.Found = new ConfirmableUser { Id = TestUserId, Confirmed = false };

        await sut.HandleAsync(new TestSendConfirmationCommand(new SendEmailConfirmationRequest(KnownEmail)));

        mocks.SentBody.Should().Contain(IssuedToken);
        mocks.SentBody.Should().NotContain("<a href", "an unconfigured base must degrade to the token, not to a broken link");
    }

    [Fact]
    public async Task Send_WhenTheTokenServiceThrottles_StillSucceeds()
    {
        var (sut, mocks) = CreateSendSut();
        sut.Found = new ConfirmableUser { Id = TestUserId, Confirmed = false };
        mocks.TokenService
            .Setup(x => x.IssueAsync(It.IsAny<string>(), It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<string>(Error.Unauthorized("Authentication.ConfirmationThrottled", "Too many.")));

        Result result = await sut.HandleAsync(new TestSendConfirmationCommand(new SendEmailConfirmationRequest(KnownEmail)));

        result.IsSuccess.Should().BeTrue();
        VerifyNoEmail(mocks);
    }

    [Fact]
    public async Task Send_WhenTheEmailFails_StillSucceeds()
    {
        var (sut, mocks) = CreateSendSut();
        sut.Found = new ConfirmableUser { Id = TestUserId, Confirmed = false };
        mocks.EmailSender
            .Setup(x => x.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("relay is down"));

        Result result = await sut.HandleAsync(new TestSendConfirmationCommand(new SendEmailConfirmationRequest(KnownEmail)));

        result.IsSuccess.Should().BeTrue("reporting the send failure to the caller would be an oracle");
    }

    // ── Confirm ──
    [Fact]
    public async Task Confirm_WithAValidToken_MarksTheAddressConfirmedAndSaves()
    {
        var user = new ConfirmableUser { Id = TestUserId, Confirmed = false };
        var (sut, mocks) = CreateConfirmSut(user);

        Result result = await sut.HandleAsync(
            new TestConfirmEmailCommand(new ConfirmEmailRequest(KnownEmail, IssuedToken)));

        result.IsSuccess.Should().BeTrue();
        user.IsEmailConfirmed.Should().BeTrue();
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_WithARejectedToken_FailsWithTheSingleGenericError()
    {
        var user = new ConfirmableUser { Id = TestUserId, Confirmed = false };
        var (sut, mocks) = CreateConfirmSut(user);
        mocks.TokenService
            .Setup(x => x.ValidateAndConsumeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<UserIdentifierType>(EmailConfirmationErrors.InvalidToken()));

        Result result = await sut.HandleAsync(
            new TestConfirmEmailCommand(new ConfirmEmailRequest(KnownEmail, "wrong")));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == EmailConfirmationErrors.InvalidTokenCode && e.Type == ErrorType.Unauthorized);
        user.IsEmailConfirmed.Should().BeFalse();
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Confirm_WhenTheAccountVanished_FailsWithTheSameGenericError()
    {
        // An unknown token and a vanished account must be indistinguishable to the caller.
        var (sut, _) = CreateConfirmSut(user: null);

        Result result = await sut.HandleAsync(
            new TestConfirmEmailCommand(new ConfirmEmailRequest(KnownEmail, IssuedToken)));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == EmailConfirmationErrors.InvalidTokenCode);
    }

    [Fact]
    public async Task Confirm_WhenTheAddressIsAlreadyConfirmed_SucceedsWithoutWriting()
    {
        // A confirmation link opened twice (a mail-client prefetch, a double tap) is the most ordinary
        // duplicate this feature has.
        var user = new ConfirmableUser { Id = TestUserId, Confirmed = true };
        var (sut, mocks) = CreateConfirmSut(user);

        Result result = await sut.HandleAsync(
            new TestConfirmEmailCommand(new ConfirmEmailRequest(KnownEmail, IssuedToken)));

        result.IsSuccess.Should().BeTrue();
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Confirm_WhenTheAggregateRefuses_SurfacesThatFailureUnchanged()
    {
        var user = new ConfirmableUser
        {
            Id = TestUserId,
            Confirmed = false,
            ForcedFailure = Error.Invariant("User.Erased", "An erased account cannot be confirmed."),
        };
        var (sut, mocks) = CreateConfirmSut(user);

        Result result = await sut.HandleAsync(
            new TestConfirmEmailCommand(new ConfirmEmailRequest(KnownEmail, IssuedToken)));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "User.Erased");
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Confirm_ConsumesTheTokenBeforeTheSave()
    {
        // Leaving the token live until the write succeeds opens a replay window in which the same
        // token redeems twice.
        var user = new ConfirmableUser { Id = TestUserId, Confirmed = false };
        var (sut, mocks) = CreateConfirmSut(user);

        await sut.HandleAsync(new TestConfirmEmailCommand(new ConfirmEmailRequest(KnownEmail, IssuedToken)));

        mocks.TokenService.Verify(
            x => x.ValidateAndConsumeAsync(KnownEmail, IssuedToken, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Helpers ──
    private static void VerifyNoEmail(HandlerMocks mocks) =>
        mocks.EmailSender.Verify(
            x => x.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);

    private static (TestSendConfirmationHandler Sut, HandlerMocks Mocks) CreateSendSut(
        string confirmationUrl = ConfirmationUrl)
    {
        var mocks = new HandlerMocks();

        mocks.TokenService
            .Setup(x => x.IssueAsync(It.IsAny<string>(), It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(IssuedToken));

        mocks.EmailSender
            .Setup(x => x.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((string to, string subject, string body, bool isHtml, CancellationToken _) =>
            {
                mocks.SentTo = to;
                mocks.SentBody = body;
                mocks.SentAsHtml = isHtml;
            })
            .Returns(Task.CompletedTask);

        var sut = new TestSendConfirmationHandler(
            mocks.UnitOfWork.Object,
            mocks.TokenService.Object,
            mocks.EmailSender.Object,
            Options.Create(new EmailConfirmationSettings { ConfirmationUrl = confirmationUrl }));

        return (sut, mocks);
    }

    private static (TestConfirmEmailHandler Sut, HandlerMocks Mocks) CreateConfirmSut(ConfirmableUser? user)
    {
        var mocks = new HandlerMocks();
        var repository = new Mock<IRepository<ConfirmableUser, UserIdentifierType>>();

        repository.Setup(x => x.GetByIdAsync(TestUserId, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        mocks.UnitOfWork.Setup(x => x.GetRepository<ConfirmableUser, UserIdentifierType>()).Returns(repository.Object);
        mocks.UnitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        mocks.TokenService
            .Setup(x => x.ValidateAndConsumeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(TestUserId));

        return (new TestConfirmEmailHandler(mocks.UnitOfWork.Object, mocks.TokenService.Object), mocks);
    }

    private sealed class HandlerMocks
    {
        public Mock<IUnitOfWork> UnitOfWork { get; } = new();

        public Mock<IEmailConfirmationTokenService> TokenService { get; } = new();

        public Mock<IEmailSender> EmailSender { get; } = new();

        public string? SentTo { get; set; }

        public string? SentBody { get; set; }

        public bool SentAsHtml { get; set; }
    }
}

/// <summary>App-side send-confirmation command shape (the shared request payload only).</summary>
public sealed record TestSendConfirmationCommand(SendEmailConfirmationRequest Request)
    : ICommandWithRequest<SendEmailConfirmationRequest>;

/// <summary>App-side confirm-email command shape.</summary>
public sealed record TestConfirmEmailCommand(ConfirmEmailRequest Request)
    : ICommandWithRequest<ConfirmEmailRequest>;

/// <summary>Minimal user aggregate carrying the email-confirmation contract.</summary>
public sealed class ConfirmableUser : AuditableAggregateRootEntity<UserIdentifierType>, IEmailConfirmableUser
{
    /// <summary>Whether the address has been proved.</summary>
    public bool Confirmed { get; set; }

    /// <summary>Forces the next <see cref="ConfirmEmail"/> to fail with this error.</summary>
    public Error? ForcedFailure { get; set; }

    /// <inheritdoc />
    public bool IsEmailConfirmed => Confirmed;

    /// <inheritdoc />
    public Result ConfirmEmail()
    {
        if (ForcedFailure is { } error)
        {
            return Result.Failure(error);
        }

        Confirmed = true;
        return Result.Success();
    }
}

/// <summary>Concrete subclass standing in for an app's <c>SendEmailConfirmationHandler</c>.</summary>
public sealed class TestSendConfirmationHandler(
    IUnitOfWork unitOfWork,
    IEmailConfirmationTokenService tokenService,
    IEmailSender emailSender,
    IOptions<EmailConfirmationSettings> settings)
    : SendEmailConfirmationHandlerBase<ConfirmableUser, TestSendConfirmationCommand>(
        unitOfWork, tokenService, emailSender, settings, NullLogger.Instance)
{
    /// <summary>The account the lookup hook resolves, or <see langword="null"/> for an unknown address.</summary>
    public ConfirmableUser? Found { get; set; }

    /// <summary>How many times the app lookup hook ran.</summary>
    public int LookupCount { get; private set; }

    protected override Task<ConfirmableUser?> FindUntrackedByEmailAsync(Email email, CancellationToken cancellationToken)
    {
        LookupCount++;
        return Task.FromResult(Found);
    }
}

/// <summary>Concrete subclass standing in for an app's <c>ConfirmEmailHandler</c>.</summary>
public sealed class TestConfirmEmailHandler(IUnitOfWork unitOfWork, IEmailConfirmationTokenService tokenService)
    : ConfirmEmailHandlerBase<ConfirmableUser, TestConfirmEmailCommand>(unitOfWork, tokenService, NullLogger.Instance);
