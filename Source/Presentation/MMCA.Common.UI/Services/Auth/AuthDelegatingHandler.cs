using System.Net.Http.Headers;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// HTTP message handler that attaches the stored JWT Bearer token to every outgoing API request.
/// Registered in the <c>"APIClient"</c> HttpClient pipeline via <c>AddHttpMessageHandler</c>.
/// A request that sets <see cref="SkipBearer"/> to <see langword="true"/> passes through untouched,
/// without reading the token storage at all, and so does a request that already carries an
/// <c>Authorization</c> header: the forced-refresh replay sets the token it just acquired, and
/// replacing it with the stored one would resend the token the server just rejected.
/// </summary>
public sealed class AuthDelegatingHandler(
    ITokenStorageService tokenStorageService) : DelegatingHandler
{
    /// <summary>
    /// Request option that opts one request out of the bearer header. The token refresh POST sets it:
    /// that endpoint is anonymous, and reading the token storage from inside a refresh would re-enter
    /// the very acquisition that is waiting for this request to complete.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> SkipBearer = new("MMCA.Common.UI.Auth.SkipBearer");

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(SkipBearer, out var skip) && skip)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        if (request.Headers.Authorization is null)
        {
            var token = await tokenStorageService.GetAccessTokenAsync();
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
