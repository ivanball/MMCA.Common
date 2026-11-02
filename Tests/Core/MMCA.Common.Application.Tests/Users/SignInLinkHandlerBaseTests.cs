using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.SignInLinks;
using MMCA.Common.Application.Interfaces.Infrastructure.Mail;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.Users.UseCases.SignInLinks;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Shared.ValueObjects.Contact;
using Moq;

namespace MMCA.Common.Application.Tests.Users;

/// <summary>
/// Exercises the two shared sign-in-link use-case bases: the send side is an anti-enumeration success
/// whatever happens and never creates an account, and the redeem side spends the token first,
/// collapses every rejection to one error, confirms the address, creates an invited account only
/// through the app hook, and opens the session through the authentication service.
/// </summary>
public sealed class SignInLinkHandlerBaseTests
{
    private const string KnownEmail = "user@example.com";
    private const string SignInUrl = "https://app.example.com/auth/sign-in-link";
    private const string IssuedToken = "issued-sign-in-token";
    private const UserIdentifierType TestUserId = 31;

    private static readonly AuthenticationResponse Issued = new("access", "refresh", DateTime.UtcNow.AddMinutes(15));

    // ── Send ──
    [Fact]
    public async Task Send_WhenDisabled_SucceedsWithoutLookupOrEmail()
    {
        var (sut, mocks) = CreateSendSut(enabled: false);

        Result result = await sut.HandleAsync(new TestSendSignInLinkCommand(new SendSignInLinkRequest(KnownEmail)));

        result.IsSuccess.Should().BeTrue();
        sut.LookupCount.Should().Be(0);
        VerifyNoEmail(mocks);
    }

    [Fact]
    public async Task Send_WhenTheAddressIsUnknown_SucceedsWithoutIssuingOrEmailing()
    {
        var (sut, mocks) = CreateSendSut();

        Result result = await sut.HandleAsync(new TestSendSignInLinkCommand(new SendSignInLinkRequest(KnownEmail)));

        result.IsSuccess.Should().BeTrue("an unknown address must be indistinguishable from a known one");
        mocks.TokenService.Verify(
            x => x.IssueAsync(It.IsAny<string>(), It.IsAny<SignInLinkPurpose>(), It.IsAny<SignInLinkProfile?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        VerifyNoEmail(mocks);
    }

    [Fact]
    public async Task Send_ForAKnownAccount_EmailsAFragmentLinkForTheSignInPurpose()
    {
        var (sut, mocks) = CreateSendSut();
        sut.Found = new ConfirmableUser { Id = TestUserId };

        Result result = await sut.HandleAsync(new TestSendSignInLinkCommand(new SendSignInLinkRequest(KnownEmail)));

        result.IsSuccess.Should().BeTrue();
        mocks.TokenService.Verify(
            x => x.IssueAsync(KnownEmail, SignInLinkPurpose.SignIn, null, It.IsAny<CancellationToken>()),
            Times.Once);
        mocks.SentTo.Should().Be(KnownEmail);
        mocks.SentBody.Should().Contain(SignInUrl + "#email=", "the token rides the fragment, never the query string");
        mocks.SentBody.Should().Contain("token=" + IssuedToken);
    }

    [Fact]
    public async Task Send_WhenTheEmailFails_StillSucceeds()
    {
        var (sut, mocks) = CreateSendSut();
        sut.Found = new ConfirmableUser { Id = TestUserId };
        mocks.EmailSender
            .Setup(x => x.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("relay is down"));

        Result result = await sut.HandleAsync(new TestSendSignInLinkCommand(new SendSignInLinkRequest(KnownEmail)));

        result.IsSuccess.Should().BeTrue("reporting the send failure to the caller would be an oracle");
    }

    // ── Redeem ──
    [Fact]
    public async Task Redeem_ForAnExistingAccount_ConfirmsTheAddressAndOpensASession()
    {
        var user = new ConfirmableUser { Id = TestUserId, Confirmed = false };
        var (sut, mocks) = CreateRedeemSut(user, new SignInLinkGrant(SignInLinkPurpose.SignIn, null));

        Result<AuthenticationResponse> result = await sut.HandleAsync(RedeemCommand());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Issued);
        user.Confirmed.Should().BeTrue("redeeming the link proves the caller reads the mailbox");
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        mocks.Auth.Verify(x => x.SignInVerifiedEmailOwnerAsync(TestUserId, "10.0.0.1", "agent", It.IsAny<CancellationToken>()), Times.Once);
        sut.OwnershipProvedCount.Should().Be(1);
    }

    [Fact]
    public async Task Redeem_ForAnAlreadyConfirmedAccount_DoesNotSave()
    {
        var user = new ConfirmableUser { Id = TestUserId, Confirmed = true };
        var (sut, mocks) = CreateRedeemSut(user, new SignInLinkGrant(SignInLinkPurpose.SignIn, null));

        Result<AuthenticationResponse> result = await sut.HandleAsync(RedeemCommand());

        result.IsSuccess.Should().BeTrue();
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Redeem_WithARejectedToken_FailsWithTheGenericErrorBeforeAnyLookup()
    {
        var (sut, mocks) = CreateRedeemSut(user: null, grant: null);

        Result<AuthenticationResponse> result = await sut.HandleAsync(RedeemCommand());

        result.Errors.Should().ContainSingle(e => e.Code == SignInLinkErrors.InvalidLinkCode);
        sut.LookupCount.Should().Be(0, "looking the account up before spending the token would be an oracle");
        mocks.Auth.Verify(
            x => x.SignInVerifiedEmailOwnerAsync(It.IsAny<UserIdentifierType>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Redeem_WhenDisabled_FailsWithoutSpendingTheToken()
    {
        var (sut, mocks) = CreateRedeemSut(new ConfirmableUser { Id = TestUserId }, new SignInLinkGrant(SignInLinkPurpose.SignIn, null), enabled: false);

        Result<AuthenticationResponse> result = await sut.HandleAsync(RedeemCommand());

        result.Errors.Should().ContainSingle(e => e.Code == SignInLinkErrors.InvalidLinkCode);
        mocks.TokenService.Verify(
            x => x.ValidateAndConsumeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Redeem_ASignInLinkForAnAddressWithNoAccount_FailsWithoutProvisioning()
    {
        var (sut, _) = CreateRedeemSut(user: null, new SignInLinkGrant(SignInLinkPurpose.SignIn, null));

        Result<AuthenticationResponse> result = await sut.HandleAsync(RedeemCommand());

        result.Errors.Should().ContainSingle(e => e.Code == SignInLinkErrors.InvalidLinkCode);
        sut.ProvisionCount.Should().Be(0, "only an invitation carrying a profile may create an account");
    }

    [Fact]
    public async Task Redeem_AnInviteWithTheDefaultHook_RefusesToCreateTheAccount()
    {
        var (sut, mocks) = CreateRedeemSut(
            user: null,
            new SignInLinkGrant(SignInLinkPurpose.Invite, new SignInLinkProfile("Ada", "Lovelace")),
            provisions: false);

        Result<AuthenticationResponse> result = await sut.HandleAsync(RedeemCommand());

        result.Errors.Should().ContainSingle(e => e.Code == SignInLinkErrors.InvalidLinkCode);
        mocks.Repository.Verify(x => x.AddAsync(It.IsAny<ConfirmableUser>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Redeem_AnInviteForANewAddress_CreatesAConfirmedAccountAndOpensASession()
    {
        var (sut, mocks) = CreateRedeemSut(
            user: null,
            new SignInLinkGrant(SignInLinkPurpose.Invite, new SignInLinkProfile("Ada", "Lovelace")));

        Result<AuthenticationResponse> result = await sut.HandleAsync(RedeemCommand(acceptedTerms: true));

        result.IsSuccess.Should().BeTrue();
        sut.ProvisionCount.Should().Be(1);
        sut.LastProvisionRequest!.Value.AcceptedTerms.Should().BeTrue("the hook reads the consent the page collected");
        sut.Provisioned!.Confirmed.Should().BeTrue();
        mocks.Repository.Verify(x => x.AddAsync(sut.Provisioned, It.IsAny<CancellationToken>()), Times.Once);
        sut.ProvisionedHookCount.Should().Be(1);
        sut.OwnershipProvedCount.Should().Be(1);
    }

    [Fact]
    public async Task Redeem_WhenTheSessionIsRefused_ReturnsTheRefusal()
    {
        var (sut, mocks) = CreateRedeemSut(new ConfirmableUser { Id = TestUserId, Confirmed = true }, new SignInLinkGrant(SignInLinkPurpose.SignIn, null));
        mocks.Auth
            .Setup(x => x.SignInVerifiedEmailOwnerAsync(TestUserId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(SignInLinkErrors.SecondFactorRequired()));

        Result<AuthenticationResponse> result = await sut.HandleAsync(RedeemCommand());

        result.Errors.Should().ContainSingle(e => e.Code == SignInLinkErrors.SecondFactorRequiredCode);
    }

    // ── Helpers ──
    private static TestRedeemSignInLinkCommand RedeemCommand(bool acceptedTerms = false) =>
        new(new RedeemSignInLinkRequest(KnownEmail, IssuedToken, acceptedTerms), "10.0.0.1", "agent");

    private static void VerifyNoEmail(HandlerMocks mocks) =>
        mocks.EmailSender.Verify(
            x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);

    private static SignInLinkSettings Settings(bool enabled) => new() { Enabled = enabled, SignInUrl = SignInUrl };

    private static (TestSendSignInLinkHandler Sut, HandlerMocks Mocks) CreateSendSut(bool enabled = true)
    {
        var mocks = new HandlerMocks();
        mocks.TokenService
            .Setup(x => x.IssueAsync(It.IsAny<string>(), It.IsAny<SignInLinkPurpose>(), It.IsAny<SignInLinkProfile?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(IssuedToken));
        mocks.EmailSender
            .Setup(x => x.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((string to, string _, string body, bool _, CancellationToken _) =>
            {
                mocks.SentTo = to;
                mocks.SentBody = body;
            })
            .Returns(Task.CompletedTask);

        var sut = new TestSendSignInLinkHandler(
            mocks.UnitOfWork.Object,
            mocks.TokenService.Object,
            mocks.EmailSender.Object,
            Options.Create(Settings(enabled)));

        return (sut, mocks);
    }

    private static (TestRedeemSignInLinkHandler Sut, HandlerMocks Mocks) CreateRedeemSut(
        ConfirmableUser? user,
        SignInLinkGrant? grant,
        bool enabled = true,
        bool provisions = true)
    {
        var mocks = new HandlerMocks();

        mocks.Repository.Setup(x => x.GetByIdAsync(TestUserId, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        mocks.UnitOfWork.Setup(x => x.GetRepository<ConfirmableUser, UserIdentifierType>()).Returns(mocks.Repository.Object);
        mocks.UnitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        mocks.TokenService
            .Setup(x => x.ValidateAndConsumeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(grant is null
                ? Result.Failure<SignInLinkGrant>(SignInLinkErrors.InvalidLink())
                : Result.Success(grant));

        mocks.Auth
            .Setup(x => x.SignInVerifiedEmailOwnerAsync(It.IsAny<UserIdentifierType>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(Issued));

        var sut = new TestRedeemSignInLinkHandler(
            mocks.UnitOfWork.Object,
            mocks.TokenService.Object,
            mocks.Auth.Object,
            Options.Create(Settings(enabled)))
        {
            Found = user,
            Provisions = provisions,
        };

        return (sut, mocks);
    }

    private sealed class HandlerMocks
    {
        public Mock<IUnitOfWork> UnitOfWork { get; } = new();

        public Mock<IRepository<ConfirmableUser, UserIdentifierType>> Repository { get; } = new();

        public Mock<ISignInLinkTokenService> TokenService { get; } = new();

        public Mock<IEmailSender> EmailSender { get; } = new();

        public Mock<IAuthenticationService> Auth { get; } = new();

        public string? SentTo { get; set; }

        public string? SentBody { get; set; }
    }
}

/// <summary>App-side send-sign-in-link command shape.</summary>
public sealed record TestSendSignInLinkCommand(SendSignInLinkRequest Request)
    : ICommandWithRequest<SendSignInLinkRequest>;

/// <summary>App-side redeem-sign-in-link command shape.</summary>
public sealed record TestRedeemSignInLinkCommand(RedeemSignInLinkRequest Request, string? IpAddress, string? UserAgent)
    : IRedeemSignInLinkCommand;

/// <summary>Concrete subclass standing in for an app's <c>SendSignInLinkHandler</c>.</summary>
public sealed class TestSendSignInLinkHandler(
    IUnitOfWork unitOfWork,
    ISignInLinkTokenService tokenService,
    IEmailSender emailSender,
    IOptions<SignInLinkSettings> settings)
    : SendSignInLinkHandlerBase<ConfirmableUser, TestSendSignInLinkCommand>(
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

/// <summary>Concrete subclass standing in for an app's <c>RedeemSignInLinkHandler</c>.</summary>
public sealed class TestRedeemSignInLinkHandler(
    IUnitOfWork unitOfWork,
    ISignInLinkTokenService tokenService,
    IAuthenticationService authenticationService,
    IOptions<SignInLinkSettings> settings)
    : RedeemSignInLinkHandlerBase<ConfirmableUser, TestRedeemSignInLinkCommand>(
        unitOfWork, tokenService, authenticationService, settings, NullLogger.Instance)
{
    /// <summary>The account the lookup hook resolves, or <see langword="null"/> for an address with none.</summary>
    public ConfirmableUser? Found { get; set; }

    /// <summary>Whether the provisioning hook creates an account (true) or defers to the base (false).</summary>
    public bool Provisions { get; set; } = true;

    /// <summary>How many times the app lookup hook ran.</summary>
    public int LookupCount { get; private set; }

    /// <summary>How many times the provisioning hook ran.</summary>
    public int ProvisionCount { get; private set; }

    /// <summary>The request the provisioning hook last saw.</summary>
    public RedeemSignInLinkRequest? LastProvisionRequest { get; private set; }

    /// <summary>The account the provisioning hook created.</summary>
    public ConfirmableUser? Provisioned { get; private set; }

    /// <summary>How many times the post-provisioning hook ran.</summary>
    public int ProvisionedHookCount { get; private set; }

    /// <summary>How many times the ownership-proved hook ran.</summary>
    public int OwnershipProvedCount { get; private set; }

    protected override Task<ConfirmableUser?> FindUntrackedByEmailAsync(Email email, CancellationToken cancellationToken)
    {
        LookupCount++;
        return Task.FromResult(Found);
    }

    protected override Result<ConfirmableUser> ProvisionUser(Email email, SignInLinkProfile profile, RedeemSignInLinkRequest request)
    {
        ProvisionCount++;
        LastProvisionRequest = request;
        if (!Provisions)
        {
            return base.ProvisionUser(email, profile, request);
        }

        Provisioned = new ConfirmableUser { Id = 99, Confirmed = false };
        return Result.Success(Provisioned);
    }

    protected override Task OnUserProvisionedAsync(ConfirmableUser user, CancellationToken cancellationToken)
    {
        ProvisionedHookCount++;
        return Task.CompletedTask;
    }

    protected override Task OnEmailOwnershipProvedAsync(ConfirmableUser user, SignInLinkGrant grant, CancellationToken cancellationToken)
    {
        OwnershipProvedCount++;
        return Task.CompletedTask;
    }
}
