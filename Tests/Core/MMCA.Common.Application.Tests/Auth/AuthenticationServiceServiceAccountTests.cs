using System.Security.Claims;
using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Sessions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Shared.ValueObjects.Contact;
using MMCA.Common.Testing.Support;
using Moq;

namespace MMCA.Common.Application.Tests.Auth;

/// <summary>
/// A machine client's service account (ADR-140, spec D) obtains tokens only through the client
/// credentials grant: every interactive sign-in path of the shared workflow refuses it and opens no
/// session, even when the account somehow carries a password the hasher would accept.
/// </summary>
public sealed class AuthenticationServiceServiceAccountTests
{
    // Spec D: a service account cannot password-sign-in.
    [Fact]
    public async Task LoginAsync_RefusesAServiceAccount_EvenWithACorrectPassword()
    {
        var harness = new Harness(isServiceAccount: true);

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("svc@service.local", "correct"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("Auth.InvalidCredentials",
            "a service account must read exactly like an unknown address");
        harness.Sessions.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task LoginAsync_StillSignsInAPerson()
    {
        var harness = new Harness(isServiceAccount: false);

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("person@example.com", "correct"));

        result.IsSuccess.Should().BeTrue();
        harness.Sessions.Sessions.Should().ContainSingle();
    }

    [Fact]
    public async Task SignInVerifiedEmailOwnerAsync_RefusesAServiceAccount()
    {
        var harness = new Harness(isServiceAccount: true);

        Result<AuthenticationResponse> result = await harness.Sut.SignInVerifiedEmailOwnerAsync(Harness.UserId);

        result.IsFailure.Should().BeTrue();
        harness.Sessions.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task SignInWithPasskeyAsync_RefusesAServiceAccount()
    {
        var harness = new Harness(isServiceAccount: true);

        Result<AuthenticationResponse> result = await harness.Sut.SignInWithPasskeyAsync(Harness.UserId);

        result.IsFailure.Should().BeTrue();
        harness.Sessions.Sessions.Should().BeEmpty();
    }

    // External login is app-level and mints through IssueTokensAsync, the one choke point every
    // interactive sign-in shares, so the refusal there covers it.
    [Fact]
    public async Task AnAppLevelSignInThroughIssueTokensAsync_RefusesAServiceAccount()
    {
        var harness = new Harness(isServiceAccount: true);

        Result<AuthenticationResponse> result = await harness.Sut.ExternalLoginLikeAsync(harness.User);

        result.IsFailure.Should().BeTrue();
        harness.Sessions.Sessions.Should().BeEmpty();
    }

    private sealed class Harness
    {
        public const UserIdentifierType UserId = 1;

        public Harness(bool isServiceAccount)
        {
            User = new ServiceAccountCapableUser { Id = UserId, ServiceAccount = isServiceAccount };

            var repository = new Mock<IRepository<ServiceAccountCapableUser, UserIdentifierType>>();
            repository.Setup(x => x.GetByIdAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(User);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.GetRepository<ServiceAccountCapableUser, UserIdentifierType>()).Returns(repository.Object);

            var tokens = new Mock<ITokenService>();
            tokens.Setup(x => x.GenerateRefreshToken()).Returns(() => Guid.NewGuid().ToString("N"));
            tokens
                .Setup(x => x.GenerateAccessToken(
                    It.IsAny<UserIdentifierType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Claim>?>()))
                .Returns("access-token");

            var passwordHasher = new Mock<IPasswordHasher>();
            passwordHasher
                .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
                .Returns(true);
            var loginProtection = new Mock<ILoginProtectionService>();
            loginProtection
                .Setup(x => x.CheckLockoutAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            Sut = new ServiceAccountAuthenticationService(
                unitOfWork.Object,
                passwordHasher.Object,
                loginProtection.Object,
                new AuthenticationValidators(Valid<LoginRequest>(), Valid<RegisterRequest>(), Valid<RefreshTokenRequest>()),
                new AuthSessionIssuer(tokens.Object, Sessions, Options.Create(new RefreshSessionSettings()), TimeProvider.System),
                User);
        }

        public ServiceAccountCapableUser User { get; }

        public InMemoryRefreshSessionStore Sessions { get; } = new();

        public ServiceAccountAuthenticationService Sut { get; }

        private static IValidator<T> Valid<T>()
        {
            var validator = new Mock<IValidator<T>>();
            validator
                .Setup(x => x.ValidateAsync(It.IsAny<T>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ValidationResult());
            return validator.Object;
        }
    }
}

/// <summary>An app user that may be a service account, and that (wrongly) carries a password either way.</summary>
public sealed class ServiceAccountCapableUser : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser
{
    public byte[] PasswordHash { get; set; } = [1, 2, 3];

    public byte[] PasswordSalt { get; set; } = [4, 5, 6];

    public bool ServiceAccount { get; set; }

    public bool IsServiceAccount => ServiceAccount;
}

/// <summary>The shared workflow over one in-memory user, plus an app-level sign-in standing for ExternalLoginAsync.</summary>
public sealed class ServiceAccountAuthenticationService(
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    ILoginProtectionService loginProtection,
    AuthenticationValidators validators,
    IAuthSessionIssuer sessionIssuer,
    ServiceAccountCapableUser user)
    : AuthenticationServiceBase<ServiceAccountCapableUser>(unitOfWork, passwordHasher, loginProtection, validators, sessionIssuer)
{
    /// <summary>What an app's ExternalLoginAsync does once it has found the linked account.</summary>
    public Task<Result<AuthenticationResponse>> ExternalLoginLikeAsync(ServiceAccountCapableUser account) =>
        IssueTokensAsync(account);

    protected override Task<ServiceAccountCapableUser?> FindUntrackedByEmailAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult<ServiceAccountCapableUser?>(user);

    protected override Task<bool> EmailExistsAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    protected override Result<ServiceAccountCapableUser> CreateUser(RegisterRequest request, byte[] passwordHash, byte[] passwordSalt) =>
        Result.Failure<ServiceAccountCapableUser>(Error.Failure("Test.NoRegistration", "Not used by these tests."));

    protected override string CreateAccessToken(ServiceAccountCapableUser user) =>
        TokenService.GenerateAccessToken(user.Id, "user@example.com", "Member", "Test User");
}
