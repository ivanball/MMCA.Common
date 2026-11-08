using System.Security.Claims;

namespace MMCA.Common.API.Authentication;

/// <summary>
/// Turns the principal an OIDC handler builds from a validated ID token into the claim shape
/// <c>OAuthControllerBase.CompleteAsync</c> and the app's <c>ExternalLoginAsync</c> already read, so
/// a generic provider needs no app change.
/// <list type="bullet">
/// <item><see cref="ClaimTypes.NameIdentifier"/> (the provider key) is <c>{tid}|{oid}</c> when both
/// claims exist (Entra ID: <c>sub</c> there is pairwise per application, <c>oid</c> is the stable user
/// id within the tenant), otherwise <c>{iss}|{sub}</c>, which is unique across issuers. Never the
/// email, which a user or an administrator can change.</item>
/// <item><c>email_verified=true</c> survives only when the provider is configured with
/// <c>TrustEmail</c> AND the token asserts it, so an app that links an external login to an existing
/// account by email only does so when the operator vouched for the issuer.</item>
/// <item>The identity's authentication type is the provider name, which the controller passes as the
/// login provider.</item>
/// </list>
/// </summary>
internal static class OidcClaimNormalizer
{
    /// <summary>The claim the app reads to decide whether the provider verified the email.</summary>
    internal const string EmailVerifiedClaimType = "email_verified";

    /// <summary>Builds the normalized principal.</summary>
    /// <param name="principal">The principal built from the validated ID token.</param>
    /// <param name="providerName">The provider (scheme) name.</param>
    /// <param name="trustEmail">The provider's <c>TrustEmail</c> setting.</param>
    /// <returns>A principal carrying only the normalized claims.</returns>
    internal static ClaimsPrincipal Normalize(ClaimsPrincipal principal, string providerName, bool trustEmail)
    {
        var claims = new List<Claim>();

        var providerKey = ProviderKey(principal);
        if (providerKey is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, providerKey, ClaimValueTypes.String, providerName));
        }

        AddIfPresent(claims, providerName, ClaimTypes.Email, First(principal, "email", ClaimTypes.Email));
        AddIfPresent(claims, providerName, ClaimTypes.GivenName, First(principal, "given_name", ClaimTypes.GivenName));
        AddIfPresent(claims, providerName, ClaimTypes.Surname, First(principal, "family_name", ClaimTypes.Surname));
        AddIfPresent(claims, providerName, ClaimTypes.Name, First(principal, "name", ClaimTypes.Name));

        if (trustEmail
            && bool.TryParse(First(principal, EmailVerifiedClaimType), out var verified)
            && verified)
        {
            claims.Add(new Claim(EmailVerifiedClaimType, "true", ClaimValueTypes.Boolean, providerName));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, providerName));
    }

    private static string? ProviderKey(ClaimsPrincipal principal)
    {
        var tenantId = First(principal, "tid");
        var objectId = First(principal, "oid");
        if (!string.IsNullOrEmpty(tenantId) && !string.IsNullOrEmpty(objectId))
        {
            return $"{tenantId}|{objectId}";
        }

        var subject = principal.FindFirst("sub") ?? principal.FindFirst(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(subject?.Value))
        {
            return null;
        }

        // The token handler stamps every claim's Issuer with the token's iss, so it stands in when the
        // iss claim itself was not kept.
        var issuer = First(principal, "iss");
        if (string.IsNullOrEmpty(issuer)
            && !string.Equals(subject.Issuer, ClaimsIdentity.DefaultIssuer, StringComparison.Ordinal))
        {
            issuer = subject.Issuer;
        }

        return string.IsNullOrEmpty(issuer) ? null : $"{issuer}|{subject.Value}";
    }

    private static string? First(ClaimsPrincipal principal, params string[] claimTypes) =>
        claimTypes
            .Select(type => principal.FindFirst(type)?.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static void AddIfPresent(List<Claim> claims, string issuer, string claimType, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            claims.Add(new Claim(claimType, value, ClaimValueTypes.String, issuer));
        }
    }
}
