using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Authorization.MultiFactor;
using MMCA.Common.API.Startup;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Tests.Authorization.MultiFactor;

/// <summary>Which framework bearer registration the host uses.</summary>
public enum BearerRegistration
{
    /// <summary><c>AddCommonAuthentication</c>, in-process HS256 validation.</summary>
    Common,

    /// <summary><c>AddForwardedJwtBearer</c>, RS256 against an authority's published keys (served statically here).</summary>
    Forwarded,
}

/// <summary>What one host is built with.</summary>
/// <param name="RequiredForRoles">The bound <c>Authorization:MultiFactor:RequiredForRoles</c> list.</param>
/// <param name="Registration">The framework bearer registration.</param>
/// <param name="RawClaims">Turns inbound claim mapping off, so the principal's claims are the token's claims verbatim.</param>
/// <param name="RoleClaimType">The identity's own role claim type, when the host changes it.</param>
/// <param name="RegisterPoliciesTwice">Calls <c>AddAuthorizationPolicies</c> once more after the bearer registration.</param>
/// <param name="HostTokenValidated">Sets a host <c>OnTokenValidated</c> handler that stamps <see cref="MultiFactorBearerHost.HostHookClaim"/>.</param>
internal sealed record BearerHostOptions(
    string[] RequiredForRoles,
    BearerRegistration Registration = BearerRegistration.Common,
    bool RawClaims = false,
    string? RoleClaimType = null,
    bool RegisterPoliciesTwice = false,
    bool HostTokenValidated = false);

/// <summary>A claim as the report carries it.</summary>
/// <param name="Type">The claim type.</param>
/// <param name="Value">The claim value.</param>
internal sealed record ClaimPair(string Type, string Value);

/// <summary>What every role and permission reader of the request sees on the validated principal.</summary>
/// <param name="Claims">Every claim of the principal, in order.</param>
/// <param name="IsInRoleAdmin">The BCL <see cref="ClaimsPrincipal.IsInRole(string)"/> for the admin role.</param>
/// <param name="HasRoleAdmin"><see cref="ClaimsPrincipalExtensions.HasRole"/> for the admin role.</param>
/// <param name="RoleValues"><see cref="ClaimsPrincipalExtensions.GetRoleValues"/>.</param>
/// <param name="PermissionClaimsHeld">The known permissions <see cref="ClaimsPrincipalExtensions.HasPermissionClaim"/> answers true for.</param>
/// <param name="WithheldRoles"><see cref="ClaimsPrincipalExtensions.GetMultiFactorWithheldRoles"/>.</param>
/// <param name="HasWithheldRole"><see cref="ClaimsPrincipalExtensions.HasMultiFactorWithheldRole"/>.</param>
/// <param name="CurrentUserRoles">The roles the current-user service reports (what a background-work origin captures).</param>
/// <param name="CurrentUserIsInRoleAdmin">The current-user service's role check (what the owner-or-admin bypass asks).</param>
/// <param name="CurrentUserPermissions">The permission claims of the current-user service's principal (what a background-work origin captures).</param>
internal sealed record PrincipalReport(
    IReadOnlyList<ClaimPair> Claims,
    bool IsInRoleAdmin,
    bool HasRoleAdmin,
    IReadOnlyList<string> RoleValues,
    IReadOnlyList<string> PermissionClaimsHeld,
    IReadOnlyList<string> WithheldRoles,
    bool HasWithheldRole,
    IReadOnlyList<string> CurrentUserRoles,
    bool CurrentUserIsInRoleAdmin,
    IReadOnlyList<string> CurrentUserPermissions)
{
    public IReadOnlyList<string> ValuesOf(string type) =>
        [.. Claims.Where(claim => string.Equals(claim.Type, type, StringComparison.Ordinal)).Select(claim => claim.Value)];
}

/// <summary>
/// A real ASP.NET Core host whose user scheme is the framework's own JWT bearer registration
/// (<c>AddCommonAuthentication</c> or <c>AddForwardedJwtBearer</c>), validating tokens this class
/// signs. It serves a report of what the validated principal looks like to every role and permission
/// reader, the same report on a hub path (token in the <c>access_token</c> query), two permission
/// endpoints, an <see cref="OwnerOrAdminFilter"/>-guarded MVC action and a second bearer scheme
/// standing in for the service-token scheme.
/// </summary>
internal sealed class MultiFactorBearerHost : IAsyncDisposable
{
    public const string Issuer = "https://identity.test";
    public const string Audience = "mmca-api";
    public const string Admin = "Admin";
    public const string Member = "Member";
    public const string Auditor = "Auditor";
    public const string OrdersManage = "orders:manage";
    public const string UsersManage = "users:manage";
    public const string ReportsRead = "reports:read";
    public const string DirectGrant = "exports:run";
    public const string OwnerClaim = "customer_id";
    public const string HostHookClaim = "host_hook";
    public const string ServiceScheme = "ServiceTokenStandIn";

    public const string ReportPath = "/whoami";
    public const string HubReportPath = "/hubs/probe";
    public const string ServiceReportPath = "/internal/whoami";
    public const string OrdersPath = "/orders";
    public const string ReportsPath = "/reports";
    public const string CustomersPath = "/customers";

    private static readonly string[] KnownPermissions = [OrdersManage, UsersManage, ReportsRead, DirectGrant];

    private static readonly byte[] HmacKey = SHA256.HashData("mmca-multi-factor-bearer-tests-signing-key"u8);

    private static readonly RsaSecurityKey RsaKey = new(RSA.Create(2048));

    private WebApplication _app = null!;

    private MultiFactorBearerHost(BearerHostOptions options) => Options = options;

    public BearerHostOptions Options { get; }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    public static async Task<MultiFactorBearerHost> StartAsync(BearerHostOptions options)
    {
        var host = new MultiFactorBearerHost(options);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        Dictionary<string, string?> configuration = new(StringComparer.Ordinal)
        {
            ["Jwt:SigningAlgorithm"] = "HS256",
            ["Jwt:SecretForKey"] = Convert.ToBase64String(HmacKey),
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
        };
        for (var i = 0; i < options.RequiredForRoles.Length; i++)
        {
            configuration[string.Create(CultureInfo.InvariantCulture, $"{MultiFactorAuthorizationSettings.SectionName}:RequiredForRoles:{i}")] =
                options.RequiredForRoles[i];
        }

        builder.Configuration.AddInMemoryCollection(configuration);

        if (options.Registration == BearerRegistration.Common)
        {
            builder.Services.AddCommonAuthentication(builder.Configuration);
        }
        else
        {
            builder.Services.AddForwardedJwtBearer(Issuer, Audience, builder.Configuration, builder.Environment);

            // The authority's discovery document and keys, supplied statically so nothing is fetched.
            builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, bearer =>
            {
                var discovery = new OpenIdConnectConfiguration { Issuer = Issuer };
                discovery.SigningKeys.Add(RsaKey);
                bearer.Configuration = discovery;
            });
        }

        builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, bearer =>
        {
            if (options.RawClaims)
            {
                bearer.MapInboundClaims = false;
            }

            if (options.RoleClaimType is { } roleClaimType)
            {
                bearer.TokenValidationParameters.RoleClaimType = roleClaimType;
            }

            if (options.HostTokenValidated)
            {
                var framework = bearer.Events?.OnTokenValidated;
                bearer.Events ??= new JwtBearerEvents();
                bearer.Events.OnTokenValidated = async context =>
                {
                    if (framework is not null)
                    {
                        await framework(context);
                    }

                    // A host enrichment step that runs before the withholding: it stamps a marker and
                    // grants the admin role from "elsewhere", which the withholding must still take.
                    var identity = (ClaimsIdentity)context.Principal!.Identity!;
                    identity.AddClaim(new Claim(HostHookClaim, "ran"));
                    identity.AddClaim(new Claim(ClaimTypes.Role, Admin));
                };
            }
        });

        // A second bearer scheme, as the service-token scheme is registered: HS256 against the shared key.
        builder.Services.AddAuthentication().AddJwtBearer(ServiceScheme, bearer =>
        {
            bearer.MapInboundClaims = false;
            bearer.TokenValidationParameters = ValidationParameters(new SymmetricSecurityKey(HmacKey), SecurityAlgorithms.HmacSha256);
        });

        if (options.RegisterPoliciesTwice)
        {
            builder.Services.AddAuthorizationPolicies();
        }

        builder.Services.AddPermissions(grants => grants
            .Grant(Admin, OrdersManage, UsersManage, ReportsRead)
            .Grant(Member, ReportsRead)
            .Grant(Auditor, ReportsRead));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
        builder.Services.AddScoped<OwnerOrAdminFilter>();
        builder.Services.Configure<OwnerOrAdminFilterOptions>(filter =>
        {
            filter.BypassRole = Admin;
            filter.OwnerClaimType = OwnerClaim;
        });
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            for (var i = manager.FeatureProviders.Count - 1; i >= 0; i--)
            {
                if (manager.FeatureProviders[i] is IApplicationFeatureProvider<ControllerFeature>)
                {
                    manager.FeatureProviders.RemoveAt(i);
                }
            }

            manager.FeatureProviders.Add(new OwnedCustomersControllerFeatureProvider());
        });

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet(ReportPath, Report).RequireAuthorization();
        app.MapGet(HubReportPath, Report).RequireAuthorization();
        app.MapGet(ServiceReportPath, Report).RequireAuthorization(policy => policy
            .AddAuthenticationSchemes(ServiceScheme)
            .RequireAuthenticatedUser());
        app.MapGet(OrdersPath, () => Results.Text("orders")).RequireAuthorization(new HasPermissionAttribute(OrdersManage));
        app.MapGet(ReportsPath, () => Results.Text("reports")).RequireAuthorization(new HasPermissionAttribute(ReportsRead));
        app.MapControllers();
        await app.StartAsync(Ct);

        host._app = app;
        return host;
    }

    /// <summary>Signs a user token for this host's registration carrying exactly <paramref name="claims"/>.</summary>
    public string CreateToken(params Claim[] claims) =>
        Options.Registration == BearerRegistration.Common
            ? Sign(new SigningCredentials(new SymmetricSecurityKey(HmacKey), SecurityAlgorithms.HmacSha256), claims)
            : Sign(new SigningCredentials(RsaKey, SecurityAlgorithms.RsaSha256), claims);

    /// <summary>Signs a token for the stand-in service scheme.</summary>
    public static string CreateServiceToken(params Claim[] claims) =>
        Sign(new SigningCredentials(new SymmetricSecurityKey(HmacKey), SecurityAlgorithms.HmacSha256), claims);

    /// <summary>The claims a token carries, as the principal sees them when inbound mapping is off.</summary>
    public static IReadOnlyList<ClaimPair> TokenClaims(string token) =>
        [.. new JsonWebTokenHandler().ReadJsonWebToken(token).Claims.Select(claim => new ClaimPair(claim.Type, claim.Value))];

    /// <summary>A client presenting <paramref name="token"/> in the Authorization header.</summary>
    public HttpClient Bearer(string token)
    {
        var client = _app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>The principal report for <paramref name="token"/> over the Authorization header.</summary>
    public async Task<PrincipalReport> ReportAsync(string token, string path = ReportPath)
    {
        using var client = Bearer(token);
        return (await client.GetFromJsonAsync<PrincipalReport>(path, Ct))!;
    }

    /// <summary>The principal report for <paramref name="token"/> on the hub path, with the token in the query and no header.</summary>
    public async Task<PrincipalReport> HubReportAsync(string token)
    {
        using var client = _app.GetTestClient();
        return (await client.GetFromJsonAsync<PrincipalReport>(HubReportPath + "?access_token=" + Uri.EscapeDataString(token), Ct))!;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private static string Sign(SigningCredentials credentials, Claim[] claims)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now,
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(10),
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = credentials,
        });
    }

    private static TokenValidationParameters ValidationParameters(SecurityKey key, string algorithm) => new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = key,
        ValidAlgorithms = [algorithm],
    };

    private static PrincipalReport Report(HttpContext context, ICurrentUserService currentUser)
    {
        var user = context.User;
        return new PrincipalReport(
            [.. user.Claims.Select(claim => new ClaimPair(claim.Type, claim.Value))],
            user.IsInRole(Admin),
            user.HasRole(Admin),
            [.. user.GetRoleValues()],
            [.. KnownPermissions.Where(user.HasPermissionClaim)],
            user.GetMultiFactorWithheldRoles(),
            user.HasMultiFactorWithheldRole(),
            [.. currentUser.Roles],
            currentUser.IsInRole(Admin),
            [.. currentUser.User.FindAll(AuthClaimTypes.Permission).Select(claim => claim.Value)]);
    }

    private sealed class OwnedCustomersControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) =>
            feature.Controllers.Add(typeof(OwnedCustomersController).GetTypeInfo());
    }
}

// Internal so the default controller discovery of other test hosts never picks it up.
[ApiController]
[Route(MultiFactorBearerHost.CustomersPath)]
[Microsoft.AspNetCore.Authorization.Authorize]
[ServiceFilter(typeof(OwnerOrAdminFilter))]
internal sealed class OwnedCustomersController : ControllerBase
{
    [HttpGet("{id:int}")]
    public IActionResult Get(int id) => Ok(id);
}
