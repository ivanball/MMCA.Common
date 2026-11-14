using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Gateway.Tests;

/// <summary>
/// Run 17 X-42: a correlation header carrying a non-ASCII byte reaches the gateway's pipeline instead
/// of being refused by Kestrel with an empty 400, so the correlation middleware can replace it. Every
/// other header keeps Kestrel's ASCII rule. Real Kestrel and raw bytes, because an HTTP client refuses
/// to send such a header and the in-memory test server never parses one.
/// </summary>
public sealed class GatewayHeaderEncodingTests
{
    // "caf\xE9": one byte outside ASCII, as a client in a Latin-1 locale sends it.
    private static readonly byte[] NonAsciiValue = [(byte)'c', (byte)'a', (byte)'f', 0xE9];

    [Fact]
    public async Task ANonAsciiCorrelationHeader_ReachesThePipeline()
    {
        await using var app = await StartGatewayAsync();

        var response = await SendRawAsync(app, "X-Correlation-ID");

        response.Should().StartWith("HTTP/1.1 200").And.Contain("seen:café");
    }

    [Fact]
    public async Task ANonAsciiByteInAnyOtherHeader_IsStillRefusedByKestrel()
    {
        await using var app = await StartGatewayAsync();

        var response = await SendRawAsync(app, "X-Other");

        response.Should().StartWith("HTTP/1.1 400");
    }

    [Fact]
    public void Apply_KeepsTheHostsOwnSelectorForEveryOtherHeader()
    {
        var options = new Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions
        {
            RequestHeaderEncodingSelector = name => name == "X-Host" ? Encoding.UTF8 : null,
        };

        GatewayHeaderEncoding.Apply(options);

        options.RequestHeaderEncodingSelector!("x-correlation-id").Should().Be(Encoding.Latin1);
        options.RequestHeaderEncodingSelector!("X-Host").Should().Be(Encoding.UTF8);
        options.RequestHeaderEncodingSelector!("Accept").Should().BeNull();
    }

    private static async Task<WebApplication> StartGatewayAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.AddReverseProxy().AddMmcaGateway(new GatewaySettings());

        var app = builder.Build();
        app.MapGet("/ping", (HttpContext context) => "seen:" + context.Request.Headers["X-Correlation-ID"]);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<string> SendRawAsync(WebApplication app, string headerName)
    {
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First());
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, address.Port, TestContext.Current.CancellationToken);
        await using var stream = tcp.GetStream();

        var request = new List<byte>();
        request.AddRange(Encoding.ASCII.GetBytes($"GET /ping HTTP/1.1\r\nHost: localhost\r\n{headerName}: "));
        request.AddRange(NonAsciiValue);
        request.AddRange(Encoding.ASCII.GetBytes("\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(request.ToArray(), TestContext.Current.CancellationToken);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}
