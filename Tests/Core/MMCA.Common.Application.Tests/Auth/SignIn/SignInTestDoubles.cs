using System.Globalization;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.EmailConfirmation;
using MMCA.Common.Application.Auth.Sessions;
using MMCA.Common.Application.Auth.TwoFactor;
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

namespace MMCA.Common.Application.Tests.Auth.SignIn;

/// <summary>
/// A <c>User</c> aggregate with password material and an address-confirmation flag. Public (not nested)
/// because Moq must proxy <c>IRepository</c> closed over it.
/// </summary>
public class SignInTestUser : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser, IEmailConfirmableUser
{
    public byte[] PasswordHash { get; set; } = [1, 2, 3];

    public byte[] PasswordSalt { get; set; } = [4, 5, 6];

    public bool Confirmed { get; set; } = true;

    public bool IsEmailConfirmed => Confirmed;

    public Result ConfirmEmail()
    {
        Confirmed = true;
        return Result.Success();
    }
}

/// <summary>
/// The smallest app-side subclass of the shared authentication workflow, generic over the aggregate so
/// the sign-in-link tests can drive a password sign-in and a refresh against the account they redeemed.
/// <see cref="SignInExternallyAsync"/> stands in for an app-level external login, which mints through
/// the protected <c>IssueTokensAsync</c> once the provider has vouched for the caller.
/// </summary>
/// <typeparam name="TUser">The aggregate.</typeparam>
public sealed class SignInAuthenticationService<TUser>(
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    ILoginProtectionService loginProtection,
    AuthenticationValidators validators,
    IAuthSessionIssuer sessionIssuer,
    ITwoFactorAuthenticator? twoFactor,
    IOptions<EmailConfirmationSettings>? emailConfirmationSettings,
    ILogger logger)
    : AuthenticationServiceBase<TUser>(unitOfWork, passwordHasher, loginProtection, validators, sessionIssuer, twoFactor, emailConfirmationSettings)
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser
{
    public TUser? UntrackedUser { get; set; }

    public Result LoginCandidateResult { get; set; } = Result.Success();

    protected override ILogger Logger => logger;

    public Task<Result<AuthenticationResponse>> SignInExternallyAsync(TUser user) =>
        IssueTokensAsync(user, "10.0.0.9", "external-agent");

    protected override Task<TUser?> FindUntrackedByEmailAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult(UntrackedUser);

    protected override Task<bool> EmailExistsAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    protected override Result<TUser> CreateUser(RegisterRequest request, byte[] passwordHash, byte[] passwordSalt) =>
        Result.Failure<TUser>(Error.Failure("Test.NoRegistration", "Registration is not under test."));

    protected override string CreateAccessToken(TUser user) =>
        string.Create(CultureInfo.InvariantCulture, $"access-{user.Id}");

    protected override Task<Result> ValidateLoginCandidateAsync(TUser untrackedUser, CancellationToken cancellationToken) =>
        Task.FromResult(LoginCandidateResult);
}

/// <summary>
/// Builds a <see cref="SignInAuthenticationService{TUser}"/> over mocks plus a real in-memory session
/// store, and records every log line the service writes.
/// </summary>
/// <typeparam name="TUser">The aggregate.</typeparam>
public sealed class SignInHarness<TUser>
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser
{
    private int _issued;

    public SignInHarness(
        ITwoFactorAuthenticator? twoFactor = null,
        bool requireConfirmedEmail = false,
        InMemoryRefreshSessionStore? sessions = null)
    {
        Sessions = sessions ?? new InMemoryRefreshSessionStore();

        UnitOfWork.Setup(x => x.GetRepository<TUser, UserIdentifierType>()).Returns(Repository.Object);
        UnitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        TokenService
            .Setup(x => x.GenerateRefreshToken())
            .Returns(() => string.Create(CultureInfo.InvariantCulture, $"refresh-{++_issued}"));
        PasswordHasher
            .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(true);
        LoginProtection
            .Setup(x => x.CheckLockoutAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var validators = new AuthenticationValidators(
            ValidValidator<LoginRequest>(),
            ValidValidator<RegisterRequest>(),
            ValidValidator<RefreshTokenRequest>());

        Sut = new SignInAuthenticationService<TUser>(
            UnitOfWork.Object,
            PasswordHasher.Object,
            LoginProtection.Object,
            validators,
            new AuthSessionIssuer(
                TokenService.Object,
                Sessions,
                Options.Create(new RefreshSessionSettings()),
                new FixedClock(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero))),
            twoFactor,
            requireConfirmedEmail ? Options.Create(new EmailConfirmationSettings { RequireConfirmedEmail = true }) : null,
            Logger);
    }

    public SignInAuthenticationService<TUser> Sut { get; }

    public Mock<IUnitOfWork> UnitOfWork { get; } = new();

    public Mock<IRepository<TUser, UserIdentifierType>> Repository { get; } = new();

    public Mock<ITokenService> TokenService { get; } = new();

    public Mock<IPasswordHasher> PasswordHasher { get; } = new();

    public Mock<ILoginProtectionService> LoginProtection { get; } = new();

    public InMemoryRefreshSessionStore Sessions { get; }

    public CapturingLogger Logger { get; } = new();

    /// <summary>Makes <paramref name="user"/> the account behind every address, for both reads.</summary>
    public void Arrange(TUser user)
    {
        Sut.UntrackedUser = user;
        Repository.Setup(x => x.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
    }

    private static IValidator<T> ValidValidator<T>()
    {
        var validator = new Mock<IValidator<T>>();
        validator
            .Setup(x => x.ValidateAsync(It.IsAny<T>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator.Object;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

/// <summary>Records every rendered log line with its level.</summary>
public sealed class CapturingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _lines = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        lock (_gate)
        {
            _lines.Add((logLevel, formatter(state, exception)));
        }
    }
}
