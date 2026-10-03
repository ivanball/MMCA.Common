using System.Globalization;
using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Legal;
using MMCA.Common.Application.Auth.Sessions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Shared.ValueObjects.Contact;
using MMCA.Common.Testing.Support;
using Moq;

namespace MMCA.Common.Application.Tests.Auth;

/// <summary>
/// Pins the Terms of Service gate on registration: with a version configured, an unticked box is
/// refused before anything is created and a ticked one reaches <c>CreateUser</c> with the configured
/// version in hand; with none configured the flag is ignored, so a host that never opted in sees
/// the registration flow it always had.
/// </summary>
public sealed class AuthenticationServiceLegalAcceptanceTests
{
    private const string ConfiguredVersion = "2026-10-01";

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    // ── Version configured ──
    [Fact]
    public async Task RegisterAsync_WithAVersionConfiguredAndTheTermsNotAccepted_IsRefusedBeforeAnythingIsCreated()
    {
        var harness = new Harness(Options.Create(new LegalAcceptanceOptions { CurrentTermsVersion = ConfiguredVersion }));

        Result<AuthenticationResponse> result = await harness.Sut.RegisterAsync(
            new RegisterRequest("new@example.com", "pw", "A", "B", AcceptedTerms: false));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == AuthErrorCodes.TermsNotAccepted);
        harness.Sut.CreateUserCalls.Should().Be(0, "no user may be created for a registrant who did not agree");
        harness.Repository.Verify(
            x => x.AddAsync(It.IsAny<TestAuthUser>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WithAVersionConfiguredAndTheTermsAccepted_RegistersAndExposesTheVersionToCreateUser()
    {
        var harness = new Harness(Options.Create(new LegalAcceptanceOptions { CurrentTermsVersion = ConfiguredVersion }));

        Result<AuthenticationResponse> result = await harness.Sut.RegisterAsync(
            new RegisterRequest("new@example.com", "pw", "A", "B", AcceptedTerms: true));

        result.IsSuccess.Should().BeTrue();
        harness.Sut.CreateUserCalls.Should().Be(1);
        harness.Sut.TermsVersionSeenByCreateUser.Should().Be(
            ConfiguredVersion,
            "CreateUser stamps the configured version on the new user");
        harness.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Version not configured ──
    [Fact]
    public async Task RegisterAsync_WithNoVersionConfigured_IgnoresTheUntickedBoxAndRegisters()
    {
        var harness = new Harness(legalAcceptance: null);

        Result<AuthenticationResponse> result = await harness.Sut.RegisterAsync(
            new RegisterRequest("new@example.com", "pw", "A", "B", AcceptedTerms: false));

        result.IsSuccess.Should().BeTrue("the feature is a no-op until a host configures a version");
        harness.Sut.CreateUserCalls.Should().Be(1);
        harness.Sut.TermsVersionSeenByCreateUser.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_WithABlankVersionConfigured_IgnoresTheUntickedBoxAndRegisters()
    {
        var harness = new Harness(Options.Create(new LegalAcceptanceOptions { CurrentTermsVersion = "  " }));

        Result<AuthenticationResponse> result = await harness.Sut.RegisterAsync(
            new RegisterRequest("new@example.com", "pw", "A", "B", AcceptedTerms: false));

        result.IsSuccess.Should().BeTrue("whitespace is the same off switch as an unset value");
        harness.Sut.TermsVersionSeenByCreateUser.Should().BeNull();
    }

    // ── AuthenticationValidators carries the option ──
    [Fact]
    public void AuthenticationValidators_BuiltWithoutTheOptions_ReportsNoTermsVersion() =>
        new AuthenticationValidators(AlwaysValid<LoginRequest>(), AlwaysValid<RegisterRequest>(), AlwaysValid<RefreshTokenRequest>())
            .CurrentTermsVersion.Should().BeNull();

    [Fact]
    public void AuthenticationValidators_BuiltWithTheOptions_ReportsTheTrimmedVersion() =>
        new AuthenticationValidators(
                AlwaysValid<LoginRequest>(),
                AlwaysValid<RegisterRequest>(),
                AlwaysValid<RefreshTokenRequest>(),
                Options.Create(new LegalAcceptanceOptions { CurrentTermsVersion = " " + ConfiguredVersion + " " }))
            .CurrentTermsVersion.Should().Be(ConfiguredVersion);

    private static IValidator<T> AlwaysValid<T>()
    {
        var validator = new Mock<IValidator<T>>();
        validator
            .Setup(x => x.ValidateAsync(It.IsAny<T>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator.Object;
    }

    /// <summary>Wires the shared workflow for a registration that succeeds unless the terms gate refuses it.</summary>
    private sealed class Harness
    {
        public Harness(IOptions<LegalAcceptanceOptions>? legalAcceptance)
        {
            UnitOfWork.Setup(x => x.GetRepository<TestAuthUser, UserIdentifierType>()).Returns(Repository.Object);
            UnitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            var issued = 0;
            var tokenService = new Mock<ITokenService>();
            tokenService
                .Setup(x => x.GenerateRefreshToken())
                .Returns(() => string.Create(CultureInfo.InvariantCulture, $"refresh-{++issued}"));

            var passwordHasher = new Mock<IPasswordHasher>();
            passwordHasher
                .Setup(x => x.HashPassword(It.IsAny<string>()))
                .Returns(([9, 9, 9], [8, 8, 8]));

            var loginProtection = new Mock<ILoginProtectionService>();
            loginProtection
                .Setup(x => x.CheckRegistrationRateLimitAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            var validators = new AuthenticationValidators(
                AlwaysValid<LoginRequest>(),
                AlwaysValid<RegisterRequest>(),
                AlwaysValid<RefreshTokenRequest>(),
                legalAcceptance);

            Sut = new TermsAwareAuthenticationService(
                UnitOfWork.Object,
                tokenService.Object,
                passwordHasher.Object,
                loginProtection.Object,
                new FixedClock(FixedNow),
                validators,
                new InMemoryRefreshSessionStore(),
                Options.Create(new RefreshSessionSettings()));
        }

        public Mock<IUnitOfWork> UnitOfWork { get; } = new();

        public Mock<IRepository<TestAuthUser, UserIdentifierType>> Repository { get; } = new();

        public TermsAwareAuthenticationService Sut { get; }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

/// <summary>
/// Concrete subclass that records what an app's <c>CreateUser</c> override can see of the terms
/// version, which is the whole contract the base offers it.
/// </summary>
public sealed class TermsAwareAuthenticationService(
    IUnitOfWork unitOfWork,
    ITokenService tokenService,
    IPasswordHasher passwordHasher,
    ILoginProtectionService loginProtection,
    TimeProvider timeProvider,
    AuthenticationValidators validators,
    IRefreshSessionStore refreshSessions,
    IOptions<RefreshSessionSettings> refreshSessionSettings)
    : AuthenticationServiceBase<TestAuthUser>(
        unitOfWork,
        passwordHasher,
        loginProtection,
        validators,
        new AuthSessionIssuer(tokenService, refreshSessions, refreshSessionSettings, timeProvider))
{
    public int CreateUserCalls { get; private set; }

    public string? TermsVersionSeenByCreateUser { get; private set; }

    protected override Task<TestAuthUser?> FindUntrackedByEmailAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult<TestAuthUser?>(null);

    protected override Task<bool> EmailExistsAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    protected override Result<TestAuthUser> CreateUser(RegisterRequest request, byte[] passwordHash, byte[] passwordSalt)
    {
        CreateUserCalls++;
        TermsVersionSeenByCreateUser = CurrentTermsVersion;
        return Result.Success(new TestAuthUser { Id = 77, PasswordHash = passwordHash, PasswordSalt = passwordSalt });
    }

    protected override string CreateAccessToken(TestAuthUser user) =>
        string.Create(CultureInfo.InvariantCulture, $"access-{user.Id}");
}
