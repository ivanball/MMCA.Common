using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace MMCA.Common.API.Authentication;

/// <summary>
/// Copies the name that Sign in with Apple posts on the FIRST authorization into the external
/// principal as <see cref="ClaimTypes.GivenName"/> and <see cref="ClaimTypes.Surname"/>.
/// <para>
/// Apple never puts the name in the ID token. It sends it once, as the <c>user</c> JSON field of the
/// form-posted callback (<c>{"name":{"firstName":"..","lastName":".."},"email":".."}</c>), and never
/// again for that Apple ID and Services ID. The AspNet.Security.OAuth.Apple handler reads only the ID
/// token, so without this hook the account is created with the "User User" placeholders and the real
/// name cannot be recovered on a later sign-in.
/// </para>
/// </summary>
internal static class AppleUserNameClaims
{
    /// <summary>Name of the callback form field that carries Apple's one-time user JSON.</summary>
    internal const string UserFormField = "user";

    /// <summary>
    /// <c>OnCreatingTicket</c> handler: reads the (already buffered) callback form and adds the name claims.
    /// </summary>
    /// <param name="context">The ticket-creation context of the Apple handler.</param>
    /// <returns>A task that completes when the claims have been added.</returns>
    internal static async Task AddFromCallbackFormAsync(OAuthCreatingTicketContext context)
    {
        if (context.Identity is null || !context.Request.HasFormContentType)
        {
            return;
        }

        var form = await context.Request.ReadFormAsync(context.HttpContext.RequestAborted).ConfigureAwait(false);
        AddFromUserField(context.Identity, form[UserFormField].ToString(), context.Options.ClaimsIssuer);
    }

    /// <summary>
    /// Adds the given name and surname from Apple's <c>user</c> JSON, leaving any claim the identity
    /// already carries untouched. A missing or malformed field adds nothing: the name is best effort and
    /// must never fail the sign-in, so the controller's placeholders apply instead.
    /// </summary>
    /// <param name="identity">The external identity built from the ID token.</param>
    /// <param name="userJson">The raw <c>user</c> form value, empty on every sign-in after the first.</param>
    /// <param name="issuer">The claims issuer of the Apple scheme.</param>
    internal static void AddFromUserField(ClaimsIdentity identity, string? userJson, string? issuer)
    {
        if (string.IsNullOrWhiteSpace(userJson))
        {
            return;
        }

        string? firstName;
        string? lastName;
        try
        {
            using var document = JsonDocument.Parse(userJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            firstName = ReadString(name, "firstName");
            lastName = ReadString(name, "lastName");
        }
        catch (JsonException)
        {
            return;
        }

        AddIfMissing(identity, ClaimTypes.GivenName, firstName, issuer);
        AddIfMissing(identity, ClaimTypes.Surname, lastName, issuer);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static void AddIfMissing(ClaimsIdentity identity, string claimType, string? value, string? issuer)
    {
        if (string.IsNullOrWhiteSpace(value) || identity.HasClaim(claim => claim.Type == claimType))
        {
            return;
        }

        identity.AddClaim(new Claim(claimType, value, ClaimValueTypes.String, issuer));
    }
}
