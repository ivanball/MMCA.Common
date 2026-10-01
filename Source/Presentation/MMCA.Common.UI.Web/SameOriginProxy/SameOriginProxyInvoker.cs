using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ServiceDiscovery.Http;
using Yarp.ReverseProxy.Forwarder;

namespace MMCA.Common.UI.Web.SameOriginProxy;

/// <summary>
/// The one outbound <see cref="HttpMessageInvoker"/> the proxy forwards through. Built the way YARP
/// requires rather than taken from <c>IHttpClientFactory</c>: no cookie container (a shared one would
/// replay one user's upstream cookies on another user's request), no automatic redirects or
/// decompression (the browser must see the upstream response as sent), and none of the host's default
/// client handlers (a retry would replay a streamed request body, and the trusted-caller header would
/// exempt every browser request from the gateway's rate limiter). When the host registered service
/// discovery (<c>AddServiceDefaults</c>), its handler wraps the transport so a discovery name such as
/// <c>https+http://gateway</c> resolves exactly as it does for the host's own clients.
/// </summary>
internal sealed class SameOriginProxyInvoker : IDisposable
{
    // The transport this instance built (and so disposes); null for a test-supplied handler.
    private readonly HttpMessageHandler? _ownedHandler;

    public SameOriginProxyInvoker(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var discovery = services.GetService<IServiceDiscoveryHttpMessageHandlerFactory>();
        _ownedHandler = CreateHandler(discovery);
        Invoker = new HttpMessageInvoker(_ownedHandler, disposeHandler: false);
    }

    /// <summary>Test hook: forwards through <paramref name="handler"/> (for example a TestServer's).</summary>
    internal SameOriginProxyInvoker(HttpMessageHandler handler) =>
        Invoker = new HttpMessageInvoker(handler, disposeHandler: false);

    public HttpMessageInvoker Invoker { get; }

    public void Dispose()
    {
        Invoker.Dispose();
        _ownedHandler?.Dispose();
    }

    private static HttpMessageHandler CreateHandler(IServiceDiscoveryHttpMessageHandlerFactory? discovery)
    {
        SocketsHttpHandler? transport = null;
        try
        {
            transport = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                ActivityHeadersPropagator = new ReverseProxyPropagator(DistributedContextPropagator.Current),
            };

            var handler = discovery?.CreateHandler(transport) ?? transport;
            transport = null;
            return handler;
        }
        finally
        {
            transport?.Dispose();
        }
    }
}
