using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Aspire.Gateway;

namespace MMCA.Common.Aspire.Tests.Gateway;

/// <summary>
/// Pins item B3 on a real in-memory pipeline (<c>AddGatewayRateLimiting</c> plus
/// <c>UseGatewayRateLimiting</c>): a request proving the trusted-caller secret that forwards the end
/// visitor's address in <c>X-Forwarded-For</c> is counted in THAT visitor's per-IP window
/// (<see cref="TrustedCallerMode.PerVisitor"/>, the default), a different visitor is unaffected,
/// <see cref="TrustedCallerMode.Exempt"/> restores the exemption, and a trusted call with no visitor
/// address stays exempt. Every request leaves from one UI-host address, the shape that made the
/// exemption necessary.
/// </summary>
[Collection(InProcessHttpPipelineCollection.Name)]
public sealed class GatewayTrustedCallerPerVisitorTests
{
    private const string Secret = "trusted-internal-caller-secret-for-tests-0";
    private const string TrustedHeader = "X-Internal-Caller-Key";
    private const string ApiPath = "/api/orders";
    private const string UiHostIp = "10.0.0.5";
    private const string VisitorA = "203.0.113.50";
    private const string VisitorB = "203.0.113.51";
    private const int Window = 3;

    [Fact]
    public void TrustedCallerMode_DefaultsToPerVisitor() =>
        new GatewayRateLimitingSettings().TrustedCallerMode.Should().Be(TrustedCallerMode.PerVisitor);

    [Fact]
    public async Task PerVisitor_TheRequestPastTheVisitorsWindowIsRefused_AndAnotherVisitorIsUnaffected()
    {
        await using var app = await StartAsync(mode: null);
        var server = app.GetTestServer();

        var visitorA = new List<int>();
        for (var i = 0; i < Window + 1; i++)
        {
            visitorA.Add(await SendAsync(server, UiHostIp, trusted: true, forwardedFor: VisitorA));
        }

        var visitorB = await SendAsync(server, UiHostIp, trusted: true, forwardedFor: VisitorB);

        visitorA.Take(Window).Should().OnlyContain(status => status == StatusCodes.Status200OK);
        visitorA[Window].Should().Be(
            StatusCodes.Status429TooManyRequests,
            "the (N+1)th trusted-caller request for one visitor inside a window of N is refused");
        visitorB.Should().Be(StatusCodes.Status200OK, "another visitor has a window of its own");
    }

    [Fact]
    public async Task PerVisitor_TrustedCallsShareTheWindowOfTheVisitorsOwnDirectRequests()
    {
        await using var app = await StartAsync(mode: null);
        var server = app.GetTestServer();

        // Two direct requests from the visitor, then trusted calls made on the visitor's behalf.
        var direct1 = await SendAsync(server, VisitorA, trusted: false, forwardedFor: null);
        var direct2 = await SendAsync(server, VisitorA, trusted: false, forwardedFor: null);
        var viaUiHost = await SendAsync(server, UiHostIp, trusted: true, forwardedFor: VisitorA);
        var viaUiHostOverBudget = await SendAsync(server, UiHostIp, trusted: true, forwardedFor: VisitorA);

        new[] { direct1, direct2, viaUiHost }.Should().OnlyContain(status => status == StatusCodes.Status200OK);
        viaUiHostOverBudget.Should().Be(
            StatusCodes.Status429TooManyRequests,
            "the trusted call takes the same window a direct request from that visitor takes");
    }

    [Fact]
    public async Task Exempt_NoTrustedCallerRequestIsRefused()
    {
        await using var app = await StartAsync(TrustedCallerMode.Exempt);
        var server = app.GetTestServer();

        var statuses = new List<int>();
        for (var i = 0; i < Window * 4; i++)
        {
            statuses.Add(await SendAsync(server, UiHostIp, trusted: true, forwardedFor: VisitorA));
        }

        statuses.Should().OnlyContain(status => status == StatusCodes.Status200OK);
    }

    [Fact]
    public async Task PerVisitor_ATrustedCallWithNoVisitorAddress_IsExempt()
    {
        await using var app = await StartAsync(mode: null);
        var server = app.GetTestServer();

        var statuses = new List<int>();
        for (var i = 0; i < Window * 4; i++)
        {
            statuses.Add(await SendAsync(server, UiHostIp, trusted: true, forwardedFor: null));
        }

        statuses.Should().OnlyContain(status => status == StatusCodes.Status200OK, "a background call carries no visitor");
    }

    [Fact]
    public async Task AnUnprovenCaller_CannotChooseItsWindowByForwardingAddresses()
    {
        await using var app = await StartAsync(mode: null);
        var server = app.GetTestServer();

        var statuses = new List<int>();
        for (var i = 0; i < Window + 1; i++)
        {
            statuses.Add(await SendAsync(server, UiHostIp, trusted: false, forwardedFor: "198.51.100." + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        statuses[Window].Should().Be(
            StatusCodes.Status429TooManyRequests,
            "without the trusted-caller proof the request is keyed on its own address");
    }

    // The named route policies of MMCA.Common.Gateway run after this limiter and key a per-visitor
    // trusted call on the address it hands them under this Items key (the cross-package contract
    // GatewayRoutePolicyTests pins from the other side).
    [Fact]
    public async Task PerVisitor_HandsTheVisitorAddressToTheRoutePolicies()
    {
        await using var app = await StartAsync(mode: null);
        var server = app.GetTestServer();

        var context = await server.SendAsync(
            request => Prepare(request, UiHostIp, trusted: true, forwardedFor: VisitorA),
            TestContext.Current.CancellationToken);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Headers["X-Visitor-Seen-By-Route-Policies"].ToString().Should().Be(VisitorA);
    }

    private static async Task<WebApplication> StartAsync(TrustedCallerMode? mode)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GatewayRateLimiting:PermitLimit"] = Window.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["GatewayRateLimiting:WindowSeconds"] = "60",
            ["GatewayRateLimiting:TrustedCallerSecret"] = Secret,
        };
        if (mode is not null)
        {
            values["GatewayRateLimiting:TrustedCallerMode"] = mode.Value.ToString();
        }

        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddGatewayRateLimiting(builder.Configuration);

        var app = builder.Build();
        app.UseGatewayRateLimiting();
        app.MapGet(ApiPath, (HttpContext context) =>
        {
            if (context.Items.TryGetValue("MMCA.Common.Gateway.TrustedCallerVisitorIp", out var visitor))
            {
                context.Response.Headers["X-Visitor-Seen-By-Route-Policies"] = visitor?.ToString();
            }

            return Results.Text("ok");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<int> SendAsync(TestServer server, string remoteIp, bool trusted, string? forwardedFor)
    {
        var context = await server.SendAsync(
            request => Prepare(request, remoteIp, trusted, forwardedFor),
            TestContext.Current.CancellationToken);
        return context.Response.StatusCode;
    }

    private static void Prepare(HttpContext request, string remoteIp, bool trusted, string? forwardedFor)
    {
        request.Request.Method = HttpMethods.Get;
        request.Request.Path = ApiPath;
        request.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        if (trusted)
        {
            request.Request.Headers[TrustedHeader] = Secret;
        }

        if (forwardedFor is not null)
        {
            request.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }
    }
}

/// <summary>
/// Runs the test classes that serve real ASP.NET Core requests in process on their own, never beside
/// another class: the telemetry tests subscribe to the process-wide ASP.NET Core and HttpClient
/// activity sources, so a request served concurrently would land in their exported spans.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InProcessHttpPipelineCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "InProcessHttpPipeline";
}
