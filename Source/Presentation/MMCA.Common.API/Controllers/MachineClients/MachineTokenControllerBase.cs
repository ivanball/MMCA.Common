using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.Startup;
using MMCA.Common.Application.Auth.MachineClients;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.MachineClients;

namespace MMCA.Common.API.Controllers.MachineClients;

/// <summary>
/// The OAuth 2.0 token endpoint for machine clients (the client credentials grant, RFC 6749 section
/// 4.4, ADR-140). The app's sealed subclass carries the routing attributes,
/// <c>[ApiController][Route("auth/token")][ApiVersion("1.0")]</c>, and needs no body.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off means no route.</b> The action answers 404 unless the host called <c>AddMachineClients</c>
/// AND set <c>MachineClients:Enabled</c>, so a consumer can inherit this base ahead of turning the
/// feature on.
/// </para>
/// <para>
/// <b>RFC 6749 on the wire, not Problem Details.</b> The request is form-encoded with
/// <c>grant_type=client_credentials</c>; the client authenticates with HTTP Basic or with
/// <c>client_id</c> and <c>client_secret</c> form fields. Success is <c>access_token</c>,
/// <c>token_type</c> and <c>expires_in</c>; failure is <c>{"error":"invalid_client"}</c> (401),
/// <c>unsupported_grant_type</c> or <c>invalid_request</c> (400). Both carry <c>Cache-Control: no-store</c>.
/// </para>
/// <para>
/// <b>Anonymous by necessity</b> (the secret is the credential) and throttled by
/// <see cref="WebApplicationBuilderExtensions.RateLimitPolicyAuthIp"/>, as login is; the framework's
/// anonymous-endpoint architecture gate lists it explicitly.
/// </para>
/// </remarks>
public abstract class MachineTokenControllerBase : ApiControllerBase
{
    /// <summary>Issues an access token to a machine client.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The RFC 6749 token response, or an RFC 6749 error response.</returns>
    [HttpPost]
    [NonIdempotent("Token issuance: a replayed response would hand a retrying caller a token minted for an earlier request, and the replay cache would hold a bearer token at rest.")]
    [AllowAnonymous]
    [EnableRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp)]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType(typeof(MachineTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MachineTokenErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MachineTokenErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    public virtual async Task<IActionResult> IssueTokenAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.RequestServices.GetService<IMachineClientService>() is not { IsEnabled: true } machineClients)
        {
            return NotFound();
        }

        // RFC 6749 section 5.1 and 5.2: neither a token nor a refusal may be cached.
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        var form = Request.HasFormContentType
            ? await Request.ReadFormAsync(cancellationToken).ConfigureAwait(false)
            : FormCollection.Empty;
        var (clientId, clientSecret) = ReadClientCredentials(form);

        var result = await machineClients
            .IssueTokenAsync(form["grant_type"].ToString(), clientId, clientSecret, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? Ok(result.Value) : TokenError(result.Errors);
    }

    /// <summary>
    /// The client's credentials: HTTP Basic when the request carries it (RFC 6749 section 2.3.1, each
    /// half form-urlencoded before the base64), otherwise the <c>client_id</c> and <c>client_secret</c>
    /// form fields.
    /// </summary>
    /// <param name="form">The request form.</param>
    /// <returns>The presented id and secret, either of which may be missing.</returns>
    private (string? ClientId, string? ClientSecret) ReadClientCredentials(IFormCollection form)
    {
        if (AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var header)
            && string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            && header.Parameter is { Length: > 0 } encoded)
        {
            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch (FormatException)
            {
                return (null, null);
            }

            var separator = decoded.IndexOf(':', StringComparison.Ordinal);
            return separator < 0
                ? (null, null)
                : (FormDecode(decoded[..separator]), FormDecode(decoded[(separator + 1)..]));
        }

        return (form["client_id"].ToString(), form["client_secret"].ToString());
    }

    private static string FormDecode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    /// <summary>Maps a refusal onto the RFC 6749 error body and status.</summary>
    /// <param name="errors">The service's errors.</param>
    /// <returns>401 <c>invalid_client</c>, or 400 <c>unsupported_grant_type</c> / <c>invalid_request</c>.</returns>
    private ObjectResult TokenError(IReadOnlyList<Error> errors)
    {
        var code = errors.Count > 0 ? errors[0].Code : MachineClientErrors.InvalidClientCode;
        return code switch
        {
            MachineClientErrors.UnsupportedGrantTypeCode => BadRequest(new MachineTokenErrorResponse(MachineTokenErrorResponse.UnsupportedGrantType)),
            MachineClientErrors.InvalidRequestCode => BadRequest(new MachineTokenErrorResponse(MachineTokenErrorResponse.InvalidRequest)),
            _ => InvalidClient(),
        };
    }

    private ObjectResult InvalidClient()
    {
        // RFC 6749 section 5.2: a 401 names the authentication scheme the client may use.
        Response.Headers.WWWAuthenticate = "Basic realm=\"token\"";
        return StatusCode(StatusCodes.Status401Unauthorized, new MachineTokenErrorResponse(MachineTokenErrorResponse.InvalidClient));
    }
}
