using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Tests.Authorization.MultiFactor;

/// <summary>
/// The callers the role-level second-factor tests use, and a header scheme that turns
/// <see cref="ClaimsHeader"/> (<c>type=value;type=value</c>) into an authenticated principal so a
/// test host can present any of them.
/// </summary>
internal static class MultiFactorTestPrincipals
{
    public const string Permission = "orders:manage";
    public const string Admin = "Admin";
    public const string Member = "Member";
    public const string Auditor = "Auditor";
    public const string SchemeName = "MultiFactorTest";
    public const string ClaimsHeader = "X-Test-Claims";

    public static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, SchemeName));

    public static ClaimsPrincipal Anonymous(params Claim[] claims) =>
        new(new ClaimsIdentity(claims));

    public static Claim Role(string role, string type = ClaimTypes.Role) => new(type, role);

    public static Claim SecondFactor(string method = AuthClaimTypes.MultiFactorMethodTotp) => new(AuthClaimTypes.MultiFactor, method);

    public static Claim Machine() => new(AuthClaimTypes.TokenUse, AuthClaimTypes.TokenUseMachine);

    public static Claim PermissionClaim(string permission = Permission) => new(AuthClaimTypes.Permission, permission);

    /// <summary>The header value presenting <paramref name="claims"/> to <see cref="HeaderClaimsAuthenticationHandler"/>.</summary>
    public static string Header(params Claim[] claims) =>
        string.Join(";", claims.Select(claim => claim.Type + "=" + claim.Value));
}

/// <summary>Authenticates a request carrying <see cref="MultiFactorTestPrincipals.ClaimsHeader"/>; anything else is anonymous.</summary>
internal sealed class HeaderClaimsAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(MultiFactorTestPrincipals.ClaimsHeader, out var header) || string.IsNullOrEmpty(header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = header.ToString()
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Select(parts => new Claim(parts[0], parts.Length > 1 ? parts[1] : string.Empty));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, MultiFactorTestPrincipals.SchemeName));

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, MultiFactorTestPrincipals.SchemeName)));
    }
}
