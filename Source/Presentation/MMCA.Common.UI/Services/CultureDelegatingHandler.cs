using System.Globalization;
using System.Net.Http.Headers;

namespace MMCA.Common.UI.Services;

/// <summary>
/// Attaches the active UI culture as an <c>Accept-Language</c> header on every outgoing API request so
/// the server localizes error messages to match the user's chosen locale (ADR-027). The cross-origin
/// Gateway does not carry the culture cookie through to the services, so this header is the channel that
/// makes backend error messages come back in the selected language. Registered in the <c>"APIClient"</c>
/// HttpClient pipeline via <c>AddHttpMessageHandler</c>.
/// </summary>
public sealed class CultureDelegatingHandler : DelegatingHandler
{
    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var culture = CultureInfo.CurrentUICulture.Name;
        if (!string.IsNullOrWhiteSpace(culture))
        {
            request.Headers.AcceptLanguage.Clear();
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(culture));
        }

        return base.SendAsync(request, cancellationToken);
    }
}
