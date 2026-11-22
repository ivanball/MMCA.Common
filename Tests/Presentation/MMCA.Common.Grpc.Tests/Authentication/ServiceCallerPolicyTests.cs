using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.Grpc.Authentication;
using Xunit;

namespace MMCA.Common.Grpc.Tests.Authentication;

/// <summary>
/// Pins the service-only edge policy (item B1): an endpoint under
/// <see cref="ServiceTokenDefaults.ServiceCallerPolicy"/> admits a valid service token and refuses a
/// valid end-user JWT, while <see cref="ServiceTokenDefaults.InternalCallerPolicy"/> keeps admitting
/// both. Also pins <c>ClaimsPrincipal.IsServiceCaller()</c> as seen by a real endpoint: true for a
/// service token, false for any user token, even one that carries an <c>svc</c> claim of its own.
/// <para>
/// The in-memory host follows <see cref="ServiceTokenAuthenticationTests"/>: the user default scheme
/// shares the service key and issuer and differs only by audience.
/// </para>
/// </summary>
public sealed class ServiceCallerPolicyTests
{
    private const string UserAudience = "mmca-users";
    private const string ServicePath = "/service-only";
    private const string InternalPath = "/internal";
    private const string WhoAmIPath = "/internal/is-service-caller";

    [Fact]
    public void ServiceCallerPolicy_IsTheDocumentedName() =>
        ServiceTokenDefaults.ServiceCallerPolicy.Should().Be("ServiceCaller");

    // -- ServiceCallerPolicy --
    [Fact]
    public async Task ServiceToken_OnAServiceCallerEndpoint_IsAccepted()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        await using var app = await CreateHostAsync(key);

        var (status, _) = await GetAsync(app, ServicePath, ServiceToken(key));

        status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ValidUserJwt_OnAServiceCallerEndpoint_IsRefused()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        await using var app = await CreateHostAsync(key);
        var userToken = UserToken(key);

        // Control: the same token is a valid user token on this host.
        var (internalStatus, _) = await GetAsync(app, InternalPath, userToken);
        var (status, _) = await GetAsync(app, ServicePath, userToken);

        internalStatus.Should().Be(HttpStatusCode.OK, "the token is a valid end-user JWT");
        status.Should().BeOneOf(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden],
            "a service-only endpoint must refuse an end-user JWT");
    }

    [Fact]
    public async Task ValidUserJwtCarryingAnSvcClaim_OnAServiceCallerEndpoint_IsRefused()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        await using var app = await CreateHostAsync(key);

        var (status, _) = await GetAsync(app, ServicePath, UserToken(key, svcClaim: ServiceTokenTestData.ServiceName));

        status.Should().BeOneOf(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden],
            "only the service scheme may satisfy the policy; an svc claim inside a user token proves nothing");
    }

    [Fact]
    public async Task NoToken_OnAServiceCallerEndpoint_IsRefused()
    {
        await using var app = await CreateHostAsync(ServiceTokenTestData.NewBase64Key());

        var (status, _) = await GetAsync(app, ServicePath, token: null);

        status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ServiceTokenWithoutTheSvcClaim_OnAServiceCallerEndpoint_IsRefused()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        await using var app = await CreateHostAsync(key);

        var (status, _) = await GetAsync(app, ServicePath, ServiceTokenTestData.Mint(key, includeServiceClaim: false));

        status.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    // -- InternalCallerPolicy unchanged --
    [Fact]
    public async Task InternalCallerEndpoint_StillAcceptsBothAServiceTokenAndAUserJwt()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        await using var app = await CreateHostAsync(key);

        var (serviceStatus, _) = await GetAsync(app, InternalPath, ServiceToken(key));
        var (userStatus, _) = await GetAsync(app, InternalPath, UserToken(key));
        var (anonymousStatus, _) = await GetAsync(app, InternalPath, token: null);

        serviceStatus.Should().Be(HttpStatusCode.OK);
        userStatus.Should().Be(HttpStatusCode.OK);
        anonymousStatus.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -- IsServiceCaller() --
    [Fact]
    public async Task IsServiceCaller_ForAServiceToken_IsTrue()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        await using var app = await CreateHostAsync(key);

        var (status, body) = await GetAsync(app, WhoAmIPath, ServiceToken(key));

        status.Should().Be(HttpStatusCode.OK);
        body.Should().Be("True");
    }

    [Fact]
    public async Task IsServiceCaller_ForAUserJwt_IsFalse()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        await using var app = await CreateHostAsync(key);

        var (status, body) = await GetAsync(app, WhoAmIPath, UserToken(key));

        status.Should().Be(HttpStatusCode.OK);
        body.Should().Be("False");
    }

    [Fact]
    public async Task IsServiceCaller_ForAUserJwtCarryingAnSvcClaim_IsFalse()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        await using var app = await CreateHostAsync(key);

        var (status, body) = await GetAsync(app, WhoAmIPath, UserToken(key, svcClaim: ServiceTokenTestData.ServiceName));

        status.Should().Be(HttpStatusCode.OK, "InternalCaller admits the user token");
        body.Should().Be("False", "the claim came from the user scheme, not from a service token");
    }

    [Fact]
    public void IsServiceCaller_ForAnUnauthenticatedPrincipalCarryingAnSvcClaim_IsFalse()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ServiceTokenDefaults.ServiceClaimType, "conference")]));

        principal.IsServiceCaller().Should().BeFalse();
    }

    // -- Scaffolding --
    private static string ServiceToken(string key) =>
        new ServiceTokenProvider(Options.Create(ServiceTokenTestData.ValidSettings(key)), TimeProvider.System).GetToken();

    /// <summary>A token the user default scheme accepts (user audience), optionally carrying an svc claim.</summary>
    private static string UserToken(string signingKey, string? svcClaim = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim> { new("sub", "user-42") };
        if (svcClaim is not null)
        {
            claims.Add(new Claim(ServiceTokenDefaults.ServiceClaimType, svcClaim));
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = ServiceTokenTestData.Issuer,
            Audience = UserAudience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(signingKey)),
                SecurityAlgorithms.HmacSha256),
        });
    }

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
        builder.Services.AddServiceTokenAuthentication(builder.Configuration);

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet(ServicePath, () => Results.Text("service")).RequireAuthorization(ServiceTokenDefaults.ServiceCallerPolicy);
        app.MapGet(InternalPath, () => Results.Text("internal")).RequireAuthorization(ServiceTokenDefaults.InternalCallerPolicy);
        app.MapGet(WhoAmIPath, (HttpContext context) => Results.Text(context.User.IsServiceCaller().ToString()))
            .RequireAuthorization(ServiceTokenDefaults.InternalCallerPolicy);
        await app.StartAsync();

        return app;
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(WebApplication app, string path, string? token)
    {
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, body);
    }
}
