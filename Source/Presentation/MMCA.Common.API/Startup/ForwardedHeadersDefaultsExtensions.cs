using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace MMCA.Common.API.Startup;

/// <summary>
/// The UI-host entry point for <see cref="ForwardedHeadersDefaults"/>.
/// </summary>
public static class ForwardedHeadersDefaultsExtensions
{
    extension(IApplicationBuilder app)
    {
        /// <summary>
        /// Adopts the ingress's forwarded headers with the framework posture
        /// (<see cref="ForwardedHeadersDefaults.Create"/>). Call it FIRST in a server-rendered UI
        /// host's pipeline, before anything reads the scheme, the host or the client address
        /// (security headers, rate limiting, HTTPS redirection, antiforgery).
        /// </summary>
        /// <param name="headers">
        /// The headers to honor; <see langword="null"/> means
        /// <see cref="ForwardedHeadersDefaults.DefaultHeaders"/> (For, Proto and Host).
        /// </param>
        /// <returns>The application builder, for chaining.</returns>
        public IApplicationBuilder UseCommonUiForwardedHeaders(ForwardedHeaders? headers = null) =>
            app.UseForwardedHeaders(ForwardedHeadersDefaults.Create(headers));
    }
}
