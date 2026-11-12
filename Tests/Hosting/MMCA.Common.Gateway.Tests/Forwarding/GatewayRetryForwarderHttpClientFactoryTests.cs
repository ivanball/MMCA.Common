using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Gateway.Forwarding;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace MMCA.Common.Gateway.Tests.Forwarding;

/// <summary>
/// Item 31: the retrying forwarder factory installs the connect retry as the OUTERMOST handler of the
/// client YARP builds for each cluster, so a retried attempt goes through the whole inner pipeline again.
/// </summary>
public sealed class GatewayRetryForwarderHttpClientFactoryTests
{
    [Fact]
    public void WrapHandler_PutsTheRetryHandlerOutermost()
    {
        var settings = Options.Create(new GatewaySettings { Retry = new GatewayRetrySettings { Enabled = true } });
        var factory = new GatewayRetryForwarderHttpClientFactory(
            NullLogger<ForwarderHttpClientFactory>.Instance,
            settings,
            new FakeTimeProvider());
        using var handler = new SocketsHttpHandler();
        var context = new ForwarderHttpClientContext { ClusterId = "catalog", NewConfig = HttpClientConfig.Empty };

        // WrapHandler is YARP's protected virtual hook (Yarp.ReverseProxy 2.3.0,
        // ForwarderHttpClientFactory.WrapHandler(ForwarderHttpClientContext, HttpMessageHandler)), so the
        // override is reached by reflection rather than widened for the test.
        var wrapHandler = typeof(GatewayRetryForwarderHttpClientFactory).GetMethod(
            "WrapHandler",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(ForwarderHttpClientContext), typeof(HttpMessageHandler)]);
        wrapHandler.Should().NotBeNull();
        wrapHandler!.DeclaringType.Should().Be<GatewayRetryForwarderHttpClientFactory>("the factory overrides the hook");

        var wrapped = wrapHandler.Invoke(factory, [context, handler]);

        var retry = wrapped.Should().BeOfType<GatewayConnectRetryHandler>().Which;
        retry.InnerHandler.Should().BeSameAs(handler, "YARP's base WrapHandler returns the handler unchanged");
    }
}
