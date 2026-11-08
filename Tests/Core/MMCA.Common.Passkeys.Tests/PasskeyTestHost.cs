using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.API.Controllers.Passkeys;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Passkeys;
using MMCA.Common.Application.Auth.Sessions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Shared.ValueObjects.Contact;
using MMCA.Common.Testing.Support;
using Moq;
using IAuthenticationService = MMCA.Common.Application.Auth.IAuthenticationService;

namespace MMCA.Common.Passkeys.Tests;

/// <summary>
/// A real ASP.NET Core host over <see cref="PasskeysControllerBase"/> for the passkey ceremony tests:
/// the framework's <c>AddPasskeys</c> (Fido2 verification, the shared-cache challenges and the
/// distributed lock from <c>AddCaching</c>), and the framework's <see cref="AuthenticationServiceBase{TUser}"/>
/// and <see cref="AuthSessionIssuer"/> minting and rotating the token pair on a controllable clock. Only
/// the edges are doubles: the passkey rows and refresh sessions live in memory, access tokens are
/// unsigned JWTs (so a test reads the claims the workflow stamped, and presents them back), and a
/// caller is either such a token or whoever the <c>X-Test-*</c> headers say.
/// </summary>
internal sealed class PasskeyTestHost : IAsyncDisposable
{
    public const string RelyingPartyId = "localhost";
    public const string Origin = "https://localhost";

    private readonly WebApplication _app;

    private PasskeyTestHost(WebApplication app, PasskeyTestUsers users, InMemoryPasskeyStore store, FakeTimeProvider clock)
    {
        _app = app;
        Users = users;
        Store = store;
        Clock = clock;
    }

    public PasskeyTestUsers Users { get; }

    public InMemoryPasskeyStore Store { get; }

    /// <summary>The clock every session instant (and so every <c>auth_time</c>) is stamped from.</summary>
    public FakeTimeProvider Clock { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>Starts a host. <paramref name="enabled"/> sets <c>Passkeys:Enabled</c>; <paramref name="addPasskeys"/> false skips the call entirely.</summary>
    public static async Task<PasskeyTestHost> StartAsync(bool enabled = true, bool addPasskeys = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Passkeys:Enabled"] = enabled ? "true" : "false",
            ["Passkeys:RelyingPartyId"] = RelyingPartyId,
            ["Passkeys:RelyingPartyName"] = "Passkey Tests",
            ["Passkeys:Origins:0"] = Origin,
        });

        var users = new PasskeyTestUsers();
        var store = new InMemoryPasskeyStore();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddCaching();
        builder.Services.AddSingleton(users);
        builder.Services.AddSingleton<IPasskeyCredentialStore>(store);
        builder.Services.AddSingleton<IRefreshSessionStore, InMemoryRefreshSessionStore>();
        builder.Services.AddScoped<IAuthenticationService, PasskeyTestAuthenticationService>();
        if (addPasskeys)
        {
            builder.Services.AddPasskeys(builder.Configuration);
        }

        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            for (var i = manager.FeatureProviders.Count - 1; i >= 0; i--)
            {
                if (manager.FeatureProviders[i] is IApplicationFeatureProvider<ControllerFeature>)
                {
                    manager.FeatureProviders.RemoveAt(i);
                }
            }

            manager.FeatureProviders.Add(new RoutedPasskeysControllerFeatureProvider());
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync(Xunit.TestContext.Current.CancellationToken);
        return new PasskeyTestHost(app, users, store, clock);
    }

    /// <summary>An anonymous client.</summary>
    public HttpClient AnonymousClient() => _app.GetTestClient();

    /// <summary>
    /// A client signed in as <paramref name="userId"/> holding a brand-new access token (its <c>iat</c> is
    /// now) whose session signed in <paramref name="signedInAgo"/> ago (its <c>auth_time</c>), or that
    /// carries no <c>auth_time</c> at all when <paramref name="withAuthTime"/> is false.
    /// </summary>
    public HttpClient SignedInClient(int userId, TimeSpan? signedInAgo = null, string? multiFactor = null, bool withAuthTime = true)
    {
        var client = _app.GetTestClient();
        var now = Clock.GetUtcNow();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserHeader, userId.ToString(CultureInfo.InvariantCulture));
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IssuedAtHeader, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        if (withAuthTime)
        {
            var signedInAt = now - (signedInAgo ?? TimeSpan.FromMinutes(1));
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.AuthTimeHeader, signedInAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }

        if (multiFactor is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.MultiFactorHeader, multiFactor);
        }

        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>A client presenting <paramref name="accessToken"/> (a token this host minted) as its bearer token.</summary>
    public HttpClient BearerClient(string accessToken)
    {
        var client = _app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>Refreshes a token pair through the framework's workflow, as <c>POST auth/refresh</c> would.</summary>
    public async Task<AuthenticationResponse> RefreshAsync(AuthenticationResponse tokens)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
            .RefreshTokenAsync(new RefreshTokenRequest(tokens.AccessToken, tokens.RefreshToken));
        return result.Value;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private sealed class RoutedPasskeysControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) =>
            feature.Controllers.Add(typeof(RoutedPasskeysController).GetTypeInfo());
    }
}

// Internal so the default controller discovery of other test hosts never picks it up. Carries the
// class-level route a consumer's subclass declares.
[ApiController]
[Route("auth/passkeys")]
internal sealed class RoutedPasskeysController : PasskeysControllerBase;

/// <summary>The accounts the host knows, by id.</summary>
internal sealed class PasskeyTestUsers
{
    private readonly Dictionary<int, PasskeyTestUser> _users = [];

    public PasskeyTestUser Add(int id, bool hasPassword = true)
    {
        var user = new PasskeyTestUser { Id = id, PasswordHash = hasPassword ? [1, 2, 3] : [] };
        _users[id] = user;
        return user;
    }

    public PasskeyTestUser? Find(int id) => _users.GetValueOrDefault(id);
}

/// <summary>An account aggregate with the framework's credential contract.</summary>
public sealed class PasskeyTestUser : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser
{
    public byte[] PasswordHash { get; set; } = [];

    public byte[] PasswordSalt { get; set; } = [];
}

/// <summary>
/// The framework's shared authentication workflow over the in-memory accounts: everything the
/// passkey path runs (<c>SignInWithPasskeyAsync</c>, <c>HasSignInMethodBesidesPasskeysAsync</c>, the
/// refresh, the session issuer) is the production code.
/// </summary>
internal sealed class PasskeyTestAuthenticationService(PasskeyTestUsers users, IRefreshSessionStore sessions, TimeProvider timeProvider)
    : AuthenticationServiceBase<PasskeyTestUser>(
        UnitOfWorkOver(users),
        Mock.Of<IPasswordHasher>(),
        Mock.Of<ILoginProtectionService>(),
        new AuthenticationValidators(Valid<LoginRequest>(), Valid<RegisterRequest>(), Valid<RefreshTokenRequest>()),
        new AuthSessionIssuer(UnsignedJwtTokenService(), sessions, Options.Create(new RefreshSessionSettings()), timeProvider))
{
    protected override Task<PasskeyTestUser?> FindUntrackedByEmailAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult<PasskeyTestUser?>(null);

    protected override Task<bool> EmailExistsAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    protected override Result<PasskeyTestUser> CreateUser(RegisterRequest request, byte[] passwordHash, byte[] passwordSalt) =>
        Result.Failure<PasskeyTestUser>(Error.Failure("Test.NoRegistration", "Not used by these tests."));

    protected override string CreateAccessToken(PasskeyTestUser user) =>
        TokenService.GenerateAccessToken(user.Id, string.Create(CultureInfo.InvariantCulture, $"user{user.Id}@example.com"), "Member", "Test User");

    private static IUnitOfWork UnitOfWorkOver(PasskeyTestUsers users)
    {
        var repository = new Mock<IRepository<PasskeyTestUser, UserIdentifierType>>();
        repository
            .Setup(x => x.GetByIdAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserIdentifierType id, CancellationToken _) => users.Find(id));

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.GetRepository<PasskeyTestUser, UserIdentifierType>()).Returns(repository.Object);
        return unitOfWork.Object;
    }

    // Unsigned JWTs carrying every claim the workflow passed in, so a test reads them back off the
    // response exactly as a client would, and a refresh reads them back off the presented token.
    private static ITokenService UnsignedJwtTokenService()
    {
        var tokens = new Mock<ITokenService>();
        tokens.Setup(x => x.GenerateRefreshToken()).Returns(() => Guid.NewGuid().ToString("N"));
        tokens
            .Setup(x => x.GenerateAccessToken(
                It.IsAny<UserIdentifierType>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Claim>?>()))
            .Returns((UserIdentifierType id, string _, string _, string _, IEnumerable<Claim>? claims) =>
                new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                    claims: [new Claim(AuthClaimTypes.Subject, id.ToString(CultureInfo.InvariantCulture)), .. claims ?? []])));
        tokens
            .Setup(x => x.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns((string token) => new ClaimsPrincipal(new ClaimsIdentity(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims)));
        return tokens.Object;
    }

    private static IValidator<T> Valid<T>()
    {
        var validator = new Mock<IValidator<T>>();
        validator
            .Setup(x => x.ValidateAsync(It.IsAny<T>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator.Object;
    }
}

/// <summary>Passkey rows in memory; a row handed out is the stored instance, as an EF tracked entity would be.</summary>
internal sealed class InMemoryPasskeyStore : IPasskeyCredentialStore
{
    private readonly List<PasskeyCredential> _rows = [];

    public IReadOnlyList<PasskeyCredential> Rows => _rows;

    public Task<IReadOnlyList<PasskeyCredential>> ListForUserAsync(UserIdentifierType userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PasskeyCredential>>([.. _rows.Where(r => r.UserId == userId).OrderBy(r => r.CreatedOn)]);

    public Task<PasskeyCredential?> FindAsync(UserIdentifierType userId, Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.SingleOrDefault(r => r.UserId == userId && r.Id == id));

    public Task<PasskeyCredential?> FindByCredentialIdAsync(byte[] credentialId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.SingleOrDefault(r => r.CredentialId.AsSpan().SequenceEqual(credentialId)));

    public Task AddAsync(PasskeyCredential credential, CancellationToken cancellationToken = default)
    {
        _rows.Add(credential);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(PasskeyCredential credential, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RemoveAsync(PasskeyCredential credential, CancellationToken cancellationToken = default)
    {
        _rows.Remove(credential);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Authenticates a bearer token this host minted (its claims exactly as stamped), or else whoever the
/// <c>X-Test-*</c> headers name, with the <c>iat</c>, <c>auth_time</c> and <c>mfa</c> claims a real
/// token carries.
/// </summary>
internal sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestTokens";
    public const string UserHeader = "X-Test-User";
    public const string IssuedAtHeader = "X-Test-Iat";
    public const string AuthTimeHeader = "X-Test-AuthTime";
    public const string MultiFactorHeader = "X-Test-Mfa";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            var token = new JwtSecurityTokenHandler().ReadJwtToken(authorization["Bearer ".Length..]);
            return Task.FromResult(Success([.. token.Claims]));
        }

        if (!Request.Headers.TryGetValue(UserHeader, out var userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        List<Claim> claims =
        [
            new(AuthClaimTypes.Subject, userId.ToString()),
            new(ClaimTypes.Email, $"user{userId}@example.com"),
            new(ClaimTypes.Name, "Test User"),
            new("iat", Request.Headers[IssuedAtHeader].ToString()),
        ];
        if (Request.Headers.TryGetValue(AuthTimeHeader, out var authTime))
        {
            claims.Add(new Claim(AuthClaimTypes.AuthTime, authTime.ToString()));
        }

        if (Request.Headers.TryGetValue(MultiFactorHeader, out var method))
        {
            claims.Add(new Claim(AuthClaimTypes.MultiFactor, method.ToString()));
        }

        return Task.FromResult(Success(claims));
    }

    private static AuthenticateResult Success(List<Claim> claims) =>
        AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
}
