using System.Net.Http.Headers;

namespace MMCA.UI.Shared.Services.Auth;

/// <summary>
/// HTTP message handler that attaches the stored JWT Bearer token to every outgoing API request.
/// Registered in the <c>"APIClient"</c> HttpClient pipeline via <c>AddHttpMessageHandler</c>.
/// </summary>
public sealed class AuthDelegatingHandler(
    ITokenStorageService tokenStorageService) : DelegatingHandler
{
    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await tokenStorageService.GetAccessTokenAsync();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
