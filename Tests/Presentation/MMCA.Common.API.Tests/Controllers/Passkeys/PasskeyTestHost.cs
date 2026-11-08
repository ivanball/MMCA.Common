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
using MMCA.Common.Shared.ValueObjects.Contact;
using Moq;
using IAuthenticationService = MMCA.Common.Application.Auth.IAuthenticationService;

namespace MMCA.Common.API.Tests.Controllers.Passkeys;

/// <summary>
/// A real ASP.NET Core host over <see cref="PasskeysControllerBase"/> for the passkey ceremony tests:
/// the framework's <c>AddPasskeys</c> (Fido2 verification, the shared-cache challenges and the
/// distributed lock from <c>AddCaching</c>), and the framework's <see cref="AuthenticationServiceBase{TUser}"/>
/// minting the token pair. Only the edges are doubles: the passkey rows live in memory, the caller is
/// whoever the <c>X-Test-*</c> headers say, and access tokens are unsigned JWTs so a test can read the
/// claims the workflow stamped.
/// </summary>
internal sealed class PasskeyTestHost : IAsyncDisposable
{
    public const string RelyingPartyId = "localhost";
    public const string Origin = "https://localhost";

    private readonly WebApplication _app;

    private PasskeyTestHost(WebApplication app, PasskeyTestUsers users, InMemoryPasskeyStore store)
    {
        _app = app;
        Users = users;
        Store = store;
    }

    public PasskeyTestUsers Users { get; }

    public InMemoryPasskeyStore Store { get; }

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

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddCaching();
        builder.Services.AddSingleton(users);
        builder.Services.AddSingleton<IPasskeyCredentialStore>(store);
        builder.Services.AddScoped<IAuthenticationService, PasskeyTestAuthenticationService>();
        if (addPasskeys)
        {
            builder.Services.AddPasskeys(builder.Configuration);
        }

        builder.Services.AddAuthentication(HeaderAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, _ => { });
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
        return new PasskeyTestHost(app, users, store);
    }

    /// <summary>An anonymous client.</summary>
    public HttpClient AnonymousClient() => _app.GetTestClient();

    /// <summary>A client signed in as <paramref name="userId"/> with a token issued <paramref name="tokenAge"/> ago.</summary>
    public HttpClient SignedInClient(int userId, TimeSpan? tokenAge = null, string? multiFactor = null)
    {
        var client = _app.GetTestClient();
        var issuedAt = DateTimeOffset.UtcNow - (tokenAge ?? TimeSpan.FromMinutes(1));
        client.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.UserHeader, userId.ToString(CultureInfo.InvariantCulture));
        client.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.IssuedAtHeader, issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        if (multiFactor is not null)
        {
            client.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.MultiFactorHeader, multiFactor);
        }

        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
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
/// session issuer) is the production code.
/// </summary>
internal sealed class PasskeyTestAuthenticationService(PasskeyTestUsers users, TimeProvider timeProvider)
    : AuthenticationServiceBase<PasskeyTestUser>(
        UnitOfWorkOver(users),
        Mock.Of<IPasswordHasher>(),
        Mock.Of<ILoginProtectionService>(),
        new AuthenticationValidators(Valid<LoginRequest>(), Valid<RegisterRequest>(), Valid<RefreshTokenRequest>()),
        new AuthSessionIssuer(UnsignedJwtTokenService(), SessionStore(), Options.Create(new RefreshSessionSettings()), timeProvider))
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
    // response exactly as a client would.
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
        return tokens.Object;
    }

    private static IRefreshSessionStore SessionStore()
    {
        var store = new Mock<IRefreshSessionStore>();
        store
            .Setup(x => x.GetUnrevokedByUserAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        store.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        return store.Object;
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

/// <summary>Authenticates whoever the <c>X-Test-*</c> headers name, with the <c>iat</c> and <c>mfa</c> claims a real token carries.</summary>
internal sealed class HeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestHeaders";
    public const string UserHeader = "X-Test-User";
    public const string IssuedAtHeader = "X-Test-Iat";
    public const string MultiFactorHeader = "X-Test-Mfa";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        List<Claim> claims =
        [
            new(AuthClaimTypes.Subject, userId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Email, $"user{userId}@example.com"),
            new(ClaimTypes.Name, "Test User"),
            new("iat", Request.Headers[IssuedAtHeader].ToString()),
        ];
        if (Request.Headers.TryGetValue(MultiFactorHeader, out var method))
        {
            claims.Add(new Claim(AuthClaimTypes.MultiFactor, method.ToString()));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
