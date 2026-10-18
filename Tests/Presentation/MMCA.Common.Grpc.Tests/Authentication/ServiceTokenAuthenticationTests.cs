using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.Grpc.Authentication;
using Xunit;

namespace MMCA.Common.Grpc.Tests.Authentication;

/// <summary>
/// Pins the receiving side of the service token: <c>AddServiceTokenAuthentication(configuration)</c>
/// adds a NON-default <c>ServiceToken</c> JwtBearer scheme (issuer, audience, lifetime and key
/// validated, HS256 only, <c>svc</c> claim required) and an <c>InternalCaller</c> policy that accepts
/// either the user's default JwtBearer token or a service token. The load-bearing negative: a valid
/// service token must never satisfy a plain <c>[Authorize]</c> endpoint, even when the user scheme
/// shares its key and issuer and differs only by audience.
/// <para>
/// The in-memory host follows the <c>WebApplication</c> + <c>UseTestServer</c> pattern of the API
/// test project's authorization tests.
/// </para>
/// </summary>
public sealed class ServiceTokenAuthenticationTests
{
    private const string UserAudience = "mmca-users";
    private const string InternalPath = "/internal";
    private const string UserPath = "/user";

    // -- Registration shape --
    [Fact]
    public void Defaults_AreTheDocumentedNames()
    {
        ServiceTokenDefaults.AuthenticationScheme.Should().Be("ServiceToken");
        ServiceTokenDefaults.InternalCallerPolicy.Should().Be("InternalCaller");
        ServiceTokenDefaults.ServiceClaimType.Should().Be("svc");
    }

    [Fact]
    public async Task AddServiceTokenAuthentication_RegistersTheInternalCallerPolicyOverBothSchemes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServiceTokenAuthentication(Configuration(ServiceTokenTestData.ConfigurationFor(ServiceTokenTestData.NewBase64Key())));
        await using var provider = services.BuildServiceProvider();

        var policy = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value
            .GetPolicy(ServiceTokenDefaults.InternalCallerPolicy);

        policy.Should().NotBeNull();
        policy!.AuthenticationSchemes.Should().Equal(JwtBearerDefaults.AuthenticationScheme, ServiceTokenDefaults.AuthenticationScheme);
        policy.Requirements.Should().ContainSingle(r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task AddServiceTokenAuthentication_ConfiguresTheSchemeForHs256Only()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServiceTokenAuthentication(Configuration(ServiceTokenTestData.ConfigurationFor(ServiceTokenTestData.NewBase64Key())));
        await using var provider = services.BuildServiceProvider();

        var parameters = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(ServiceTokenDefaults.AuthenticationScheme)
            .TokenValidationParameters;

        parameters.ValidateIssuer.Should().BeTrue();
        parameters.ValidateAudience.Should().BeTrue();
        parameters.ValidateLifetime.Should().BeTrue();
        parameters.ValidateIssuerSigningKey.Should().BeTrue();
        parameters.ValidIssuer.Should().Be(ServiceTokenTestData.Issuer);
        parameters.ValidAudience.Should().Be(ServiceTokenTestData.Audience);
        parameters.ValidAlgorithms.Should().Equal(SecurityAlgorithms.HmacSha256);
    }

    [Fact]
    public async Task AddServiceTokenAuthentication_DoesNotClaimTheDefaultScheme()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServiceTokenAuthentication(Configuration(ServiceTokenTestData.ConfigurationFor(ServiceTokenTestData.NewBase64Key())));
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;

        options.DefaultScheme.Should().NotBe(ServiceTokenDefaults.AuthenticationScheme);
        options.DefaultAuthenticateScheme.Should().NotBe(ServiceTokenDefaults.AuthenticationScheme);
        options.DefaultChallengeScheme.Should().NotBe(ServiceTokenDefaults.AuthenticationScheme);
        options.Schemes.Should().Contain(s => s.Name == ServiceTokenDefaults.AuthenticationScheme);
    }

    // -- End to end over an in-memory host --
    [Fact]
    public async Task ServiceToken_OnAnInternalCallerEndpoint_IsAccepted()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        await using var app = await CreateHostAsync(key);
        var token = new ServiceTokenProvider(Options.Create(ServiceTokenTestData.ValidSettings(key)), TimeProvider.System).GetToken();

        var status = await GetAsync(app, InternalPath, token);

        status.Should().Be(HttpStatusCode.OK, "a token minted by the calling service's own provider must pass the InternalCaller policy");
    }

    [Fact]
    public async Task ServiceToken_OnAPlainAuthorizeEndpoint_IsRejected()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        await using var app = await CreateHostAsync(key);
        var token = new ServiceTokenProvider(Options.Create(ServiceTokenTestData.ValidSettings(key)), TimeProvider.System).GetToken();

        var status = await GetAsync(app, UserPath, token);

        status.Should().Be(HttpStatusCode.Unauthorized,
            "a service token must never satisfy the default user scheme, or any internal caller becomes every user");
    }

    [Fact]
    public async Task TokenSignedWithADifferentKey_OnAnInternalCallerEndpoint_IsRejected()
    {
        await using var app = await CreateHostAsync(ServiceTokenTestData.NewBase64Key());
        var token = ServiceTokenTestData.Mint(ServiceTokenTestData.NewBase64Key());

        var status = await GetAsync(app, InternalPath, token);

        status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenWithoutTheSvcClaim_OnAnInternalCallerEndpoint_IsRejected()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        await using var app = await CreateHostAsync(key);
        var token = ServiceTokenTestData.Mint(key, includeServiceClaim: false);

        var status = await GetAsync(app, InternalPath, token);

        status.Should().Be(HttpStatusCode.Unauthorized, "the svc claim is what marks a token as a service token");
    }

    [Fact]
    public async Task ExpiredServiceToken_OnAnInternalCallerEndpoint_IsRejected()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        await using var app = await CreateHostAsync(key);
        var token = ServiceTokenTestData.Mint(
            key,
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1));

        var status = await GetAsync(app, InternalPath, token);

        status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenWithTheWrongAudience_OnAnInternalCallerEndpoint_IsRejected()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        await using var app = await CreateHostAsync(key);
        var token = ServiceTokenTestData.Mint(key, audience: "someone-else");

        var status = await GetAsync(app, InternalPath, token);

        status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UserToken_OnAnInternalCallerEndpoint_IsAccepted()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        await using var app = await CreateHostAsync(key);
        var token = ServiceTokenTestData.Mint(key, audience: UserAudience, includeServiceClaim: false);

        var status = await GetAsync(app, InternalPath, token);

        status.Should().Be(HttpStatusCode.OK, "InternalCaller accepts the forwarded user token as well as a service token");
    }

    [Fact]
    public async Task UserToken_OnAPlainAuthorizeEndpoint_IsAccepted()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        await using var app = await CreateHostAsync(key);
        var token = ServiceTokenTestData.Mint(key, audience: UserAudience, includeServiceClaim: false);

        var status = await GetAsync(app, UserPath, token);

        status.Should().Be(HttpStatusCode.OK, "adding the service scheme must not disturb the user default scheme");
    }

    [Fact]
    public async Task NoToken_OnAnInternalCallerEndpoint_IsRejected()
    {
        await using var app = await CreateHostAsync(ServiceTokenTestData.NewBase64Key());

        var status = await GetAsync(app, InternalPath, token: null);

        status.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -- Scaffolding --

    // The user default scheme shares the service key AND issuer and differs only by audience, so the
    // audience (and the non-default registration) is the only thing keeping a service token out of it.
    private static async Task<WebApplication> CreateHostAsync(string signingKey)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(ServiceTokenTestData.ConfigurationFor(signingKey));

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = ServiceTokenTestData.Issuer,
                ValidAudience = UserAudience,
                IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(signingKey)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            });
        builder.Services.AddAuthorization();

        // Registered AFTER the user scheme, so a registration that claimed the default would win.
        builder.Services.AddServiceTokenAuthentication(builder.Configuration);

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet(InternalPath, () => Results.Text("internal")).RequireAuthorization(ServiceTokenDefaults.InternalCallerPolicy);
        app.MapGet(UserPath, () => Results.Text("user")).RequireAuthorization();
        await app.StartAsync();

        return app;
    }

    private static async Task<HttpStatusCode> GetAsync(WebApplication app, string path, string? token)
    {
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
