using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MMCA.Common.UI.Web.Hosting;

/// <summary>
/// Endpoint conventions for a head's <c>MapRazorComponents</c> chain.
/// </summary>
public static class RazorComponentsEndpointExtensions
{
    /// <summary>
    /// Lets every page endpoint answer <c>HEAD</c> as well as the methods it already accepts. On .NET 10
    /// a Razor component endpoint lists its methods explicitly (<c>GET</c> and <c>POST</c>) without
    /// <c>HEAD</c>, so an uptime probe, a link checker or a crawler that sends <c>HEAD /</c> gets a 404
    /// for a page a <c>GET</c> renders (Store manual test Run 4, O9). The .NET 11 endpoint already lists
    /// <c>HEAD</c>, so there the call changes nothing. The server answers a <c>HEAD</c> with the
    /// <c>GET</c> response's status and headers and no body.
    /// </summary>
    /// <remarks>
    /// Applied as a <c>Finally</c> convention, so it reads the methods the endpoint ended up with (a
    /// form-handling page keeps <c>POST</c>) and appends one <see cref="HttpMethodMetadata"/> naming
    /// them plus <c>HEAD</c>; routing honours the last method metadata on an endpoint. An endpoint that
    /// restricts no methods is left alone, since it already matches <c>HEAD</c>.
    /// </remarks>
    /// <param name="builder">The builder <c>MapRazorComponents</c> returned.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static RazorComponentsEndpointConventionBuilder AllowHead(this RazorComponentsEndpointConventionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Finally(static endpoint =>
        {
            var current = endpoint.Metadata.OfType<IHttpMethodMetadata>().LastOrDefault();
            if (current is null || current.HttpMethods.Count == 0)
            {
                return;
            }

            if (current.HttpMethods.Contains(HttpMethods.Head, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            endpoint.Metadata.Add(new HttpMethodMetadata([.. current.HttpMethods, HttpMethods.Head], current.AcceptCorsPreflight));
        });

        return builder;
    }
}
