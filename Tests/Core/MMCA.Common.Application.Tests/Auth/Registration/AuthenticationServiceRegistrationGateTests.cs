using System.Globalization;
using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.EmailConfirmation;
using MMCA.Common.Application.Auth.Sessions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Shared.ValueObjects.Contact;
using MMCA.Common.Testing.Support;
using Moq;

namespace MMCA.Common.Application.Tests.Auth.Registration;

/// <summary>
/// Registration opens no session exactly when sign-in would refuse the new account: with the host
/// requiring a confirmed address and a confirmable account that is not confirmed yet, the account is
/// created and the result is the confirmation-pending success, with no refresh session stored and no
/// token minted. With the gate off, or an account type without confirmation, registration still opens
/// a session as it always did.
/// </summary>
public sealed class AuthenticationServiceRegistrationGateTests
{
    private const string IpAddress = "10.0.0.7";

    private static RegisterRequest Request => new("new@example.com", "Password123!", "Ada", "Lovelace");

    [Fact]
    public async Task GateOn_UnconfirmedConfirmableAccount_IsCreated_ButOpensNoSessionAndMintsNoToken()
    {
        var harness = new Harness<UnconfirmedRegistrant>(requireConfirmedEmail: true, () => new UnconfirmedRegistrant { Id = 90 });

        Result<AuthenticationResponse> result = await harness.Sut.RegisterAsync(Request, IpAddress);

        result.IsSuccess.Should().BeTrue("the account exists; the caller is told to confirm the address");
        result.Value.IsConfirmationPending().Should().BeTrue();
        result.Value.AccessToken.Should().BeEmpty();
        result.Value.RefreshToken.Should().BeEmpty();
        harness.Sessions.Sessions.Should().BeEmpty("no refresh session may be stored for an unconfirmed address");
        harness.Sut.AccessTokensMinted.Should().Be(0);
        harness.TokenService.Verify(x => x.GenerateRefreshToken(), Times.Never);
        harness.Repository.Verify(x => x.AddAsync(It.IsAny<UnconfirmedRegistrant>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.Sut.RegisteredHookCalls.Should().Be(1, "the app's registration side effect (the confirmation mail) still runs");
        harness.LoginProtection.Verify(
            x => x.IncrementRegistrationCountAsync(IpAddress, It.IsAny<CancellationToken>()),
            Times.Once,
            "the registration still counts against the caller's IP");
    }

    [Fact]
    public async Task GateOn_UnconfirmedAccount_IsJudgedByTheUserTheRegistrationHookReturns()
    {
        // The hook may re-fetch the account; the gate reads the account the token would be minted for.
        var harness = new Harness<UnconfirmedRegistrant>(requireConfirmedEmail: true, () => new UnconfirmedRegistrant { Id = 91, Confirmed = true });
        harness.Sut.ReplaceOnRegistered = registered => new UnconfirmedRegistrant { Id = registered.Id, Confirmed = false };

        Result<AuthenticationResponse> result = await harness.Sut.RegisterAsync(Request, IpAddress);

        result.Value.IsConfirmationPending().Should().BeTrue();
        harness.Sessions.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task GateOff_UnconfirmedConfirmableAccount_OpensASessionAsBefore()
    {
        var harness = new Harness<UnconfirmedRegistrant>(requireConfirmedEmail: false, () => new UnconfirmedRegistrant { Id = 92 });

        Result<AuthenticationResponse> result = await harness.Sut.RegisterAsync(Request, IpAddress);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsConfirmationPending().Should().BeFalse();
        result.Value.AccessToken.Should().Be("access-92");
        harness.Sessions.Sessions.Should().ContainSingle().Which.UserId.Should().Be(92);
    }

    [Fact]
    public async Task GateSettingsAbsent_OpensASessionAsBefore()
    {
        var harness = new Harness<UnconfirmedRegistrant>(requireConfirmedEmail: null, () => new UnconfirmedRegistrant { Id = 93 });

        Result<AuthenticationResponse> result = await harness.Sut.RegisterAsync(Request, IpAddress);

        result.Value.AccessToken.Should().Be("access-93");
        harness.Sessions.Sessions.Should().ContainSingle();
    }

    [Fact]
    public async Task GateOn_AccountTypeWithoutConfirmation_OpensASessionAsBefore()
    {
        var harness = new Harness<PlainRegistrant>(requireConfirmedEmail: true, () => new PlainRegistrant { Id = 94 });

        Result<AuthenticationResponse> result = await harness.Sut.RegisterAsync(Request, IpAddress);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be("access-94", "an account type that cannot be confirmed is never held back");
        harness.Sessions.Sessions.Should().ContainSingle().Which.UserId.Should().Be(94);
    }

    [Fact]
    public async Task GateOn_ConfirmedConfirmableAccount_OpensASession()
    {
        var harness = new Harness<UnconfirmedRegistrant>(requireConfirmedEmail: true, () => new UnconfirmedRegistrant { Id = 95, Confirmed = true });

        Result<AuthenticationResponse> result = await harness.Sut.RegisterAsync(Request, IpAddress);

        result.Value.AccessToken.Should().Be("access-95");
        harness.Sessions.Sessions.Should().ContainSingle();
    }

    [Fact]
    public async Task GateOn_WithAnOverriddenGateThatPasses_OpensASession()
    {
        // The same virtual gate decides sign-in and registration, so an override moves both.
        var harness = new Harness<UnconfirmedRegistrant>(requireConfirmedEmail: true, () => new UnconfirmedRegistrant { Id = 96 });
        harness.Sut.GateOverride = Result.Success();

        Result<AuthenticationResponse> result = await harness.Sut.RegisterAsync(Request, IpAddress);

        result.Value.AccessToken.Should().Be("access-96");
        harness.Sessions.Sessions.Should().ContainSingle();
    }

    [Fact]
    public async Task GateOff_WithAnOverriddenGateThatRefuses_OpensNoSession()
    {
        var harness = new Harness<PlainRegistrant>(requireConfirmedEmail: false, () => new PlainRegistrant { Id = 97 });
        harness.Sut.GateOverride = Result.Failure(EmailConfirmationErrors.EmailNotConfirmed());

        Result<AuthenticationResponse> result = await harness.Sut.RegisterAsync(Request, IpAddress);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsConfirmationPending().Should().BeTrue();
        harness.Sessions.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task GateOn_AnAddressAlreadyRegistered_StillAnswersTheConflict()
    {
        var harness = new Harness<UnconfirmedRegistrant>(requireConfirmedEmail: true, () => new UnconfirmedRegistrant { Id = 98 });
        harness.Sut.EmailExists = true;

        Result<AuthenticationResponse> result = await harness.Sut.RegisterAsync(Request, IpAddress);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == AuthErrorCodes.EmailAlreadyExists);
        harness.Repository.Verify(x => x.AddAsync(It.IsAny<UnconfirmedRegistrant>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GateOn_AnInvalidRequest_StillAnswersTheValidationFailure()
    {
        var harness = new Harness<UnconfirmedRegistrant>(requireConfirmedEmail: true, () => new UnconfirmedRegistrant { Id = 99 }, registerValid: false);

        Result<AuthenticationResponse> result = await harness.Sut.RegisterAsync(Request, IpAddress);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().NotBeEmpty();
        harness.Repository.Verify(x => x.AddAsync(It.IsAny<UnconfirmedRegistrant>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class Harness<TUser>
        where TUser : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser
    {
        public Harness(bool? requireConfirmedEmail, Func<TUser> createUser, bool registerValid = true)
        {
            UnitOfWork.Setup(x => x.GetRepository<TUser, UserIdentifierType>()).Returns(Repository.Object);
            UnitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            var issued = 0;
            TokenService.Setup(x => x.GenerateRefreshToken())
                .Returns(() => string.Create(CultureInfo.InvariantCulture, $"refresh-{++issued}"));

            var passwordHasher = new Mock<IPasswordHasher>();
            passwordHasher.Setup(x => x.HashPassword(It.IsAny<string>())).Returns(([7, 7], [8, 8]));

            LoginProtection
                .Setup(x => x.CheckRegistrationRateLimitAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            var validators = new AuthenticationValidators(
                Validator<LoginRequest>(valid: true),
                Validator<RegisterRequest>(registerValid),
                Validator<RefreshTokenRequest>(valid: true));

            Sut = new RegistrationGateService<TUser>(
                UnitOfWork.Object,
                passwordHasher.Object,
                LoginProtection.Object,
                validators,
                new AuthSessionIssuer(
                    TokenService.Object,
                    Sessions,
                    Options.Create(new RefreshSessionSettings()),
                    new FixedClock(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero))),
                requireConfirmedEmail is { } required
                    ? Options.Create(new EmailConfirmationSettings { RequireConfirmedEmail = required })
                    : null,
                createUser);
        }

        public RegistrationGateService<TUser> Sut { get; }

        public Mock<IUnitOfWork> UnitOfWork { get; } = new();

        public Mock<IRepository<TUser, UserIdentifierType>> Repository { get; } = new();

        public Mock<ITokenService> TokenService { get; } = new();

        public Mock<ILoginProtectionService> LoginProtection { get; } = new();

        public InMemoryRefreshSessionStore Sessions { get; } = new();

        private static IValidator<T> Validator<T>(bool valid)
        {
            var validator = new Mock<IValidator<T>>();
            validator
                .Setup(x => x.ValidateAsync(It.IsAny<T>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(valid ? new ValidationResult() : new ValidationResult([new ValidationFailure("Email", "Email is required.")]));
            return validator.Object;
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

/// <summary>An account type that carries the address-confirmation contract; unconfirmed by default.</summary>
public sealed class UnconfirmedRegistrant : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser, IEmailConfirmableUser
{
    public byte[] PasswordHash { get; set; } = [1];

    public byte[] PasswordSalt { get; set; } = [2];

    public bool Confirmed { get; set; }

    public bool IsEmailConfirmed => Confirmed;

    public Result ConfirmEmail()
    {
        Confirmed = true;
        return Result.Success();
    }
}

/// <summary>An account type with no address confirmation at all.</summary>
public sealed class PlainRegistrant : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser
{
    public byte[] PasswordHash { get; set; } = [1];

    public byte[] PasswordSalt { get; set; } = [2];
}

/// <summary>
/// The smallest registering subclass: counts the access tokens it mints and the registration hook
/// calls, and lets a test override the confirmation gate or the user the hook hands back.
/// </summary>
/// <typeparam name="TUser">The aggregate.</typeparam>
public sealed class RegistrationGateService<TUser>(
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    ILoginProtectionService loginProtection,
    AuthenticationValidators validators,
    IAuthSessionIssuer sessionIssuer,
    IOptions<EmailConfirmationSettings>? emailConfirmationSettings,
    Func<TUser> createUser)
    : AuthenticationServiceBase<TUser>(unitOfWork, passwordHasher, loginProtection, validators, sessionIssuer, twoFactor: null, emailConfirmationSettings)
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser
{
    public bool EmailExists { get; set; }

    public Result? GateOverride { get; set; }

    public Func<TUser, TUser>? ReplaceOnRegistered { get; set; }

    public int AccessTokensMinted { get; private set; }

    public int RegisteredHookCalls { get; private set; }

    protected override Task<TUser?> FindUntrackedByEmailAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult<TUser?>(null);

    protected override Task<bool> EmailExistsAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult(EmailExists);

    protected override Result<TUser> CreateUser(RegisterRequest request, byte[] passwordHash, byte[] passwordSalt) =>
        Result.Success(createUser());

    protected override string CreateAccessToken(TUser user)
    {
        AccessTokensMinted++;
        return string.Create(CultureInfo.InvariantCulture, $"access-{user.Id}");
    }

    protected override Result CheckEmailConfirmed(TUser untrackedUser) =>
        GateOverride ?? base.CheckEmailConfirmed(untrackedUser);

    protected override Task<TUser> OnUserRegisteredAsync(TUser user, CancellationToken cancellationToken)
    {
        RegisteredHookCalls++;
        return Task.FromResult(ReplaceOnRegistered?.Invoke(user) ?? user);
    }
}
