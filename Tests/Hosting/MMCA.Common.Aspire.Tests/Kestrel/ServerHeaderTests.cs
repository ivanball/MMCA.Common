using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Aspire.Tests.Gateway;

namespace MMCA.Common.Aspire.Tests.Kestrel;

/// <summary>
/// Pins item B5 (D-02): a host that takes <c>AddServiceDefaults()</c> answers with no
/// <c>Server</c> response header. TestServer never writes that header, so the check runs against a
/// real Kestrel listener on a dynamic loopback port (the pattern of the warm-up tests), with a host
/// that skips the service defaults as the control proving the header would otherwise be there.
/// </summary>
[Collection(InProcessHttpPipelineCollection.Name)]
public sealed class ServerHeaderTests
{
    [Fact]
    public async Task WithServiceDefaults_TheResponseCarriesNoServerHeader()
    {
        using var response = await GetFromKestrelAsync(withServiceDefaults: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Server").Should().BeFalse("the service defaults turn the Server header off for every host");
    }

    [Fact]
    public async Task WithoutServiceDefaults_KestrelSendsAServerHeader_TheControl()
    {
        using var response = await GetFromKestrelAsync(withServiceDefaults: false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Server").Should().BeTrue("Kestrel's own default writes the header");
    }

    private static async Task<HttpResponseMessage> GetFromKestrelAsync(bool withServiceDefaults)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (withServiceDefaults)
        {
            builder.AddServiceDefaults();
        }

        // Loopback + dynamic port: Kestrel refuses dynamic binding on the "localhost" alias, and a
        // fixed port would make parallel test runs collide.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));

        await using var app = builder.Build();
        app.MapGet("/", () => Results.Text("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            return await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
