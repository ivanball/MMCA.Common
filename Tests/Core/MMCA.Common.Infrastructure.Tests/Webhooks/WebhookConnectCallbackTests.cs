using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Infrastructure.Webhooks;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Webhooks;

/// <summary>
/// <c>WebhookConnectCallback</c> (design 35.23, review amendment A1), driven through the real
/// <c>MMCA.Webhooks</c> client that <c>AddWebhooks</c> configures, because
/// <see cref="SocketsHttpConnectionContext"/> has no public constructor. The host is resolved through
/// a stub <see cref="IWebhookDnsResolver"/>; when any resolved address is not public the callback throws
/// <see cref="WebhookTargetRefusedException"/> BEFORE a socket is opened, which is what makes the
/// connect-time check authoritative over DNS rebinding. With insecure targets allowed (a Development
/// host with the flag) the check is skipped and the callback connects to the first address.
/// </summary>
public sealed class WebhookConnectCallbackTests
{
    private const string ClientName = "MMCA.Webhooks";

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("192.0.2.10", "10.1.2.3")]
    public async Task PrivateResolution_ThrowsBeforeConnect(params string[] addresses)
    {
        var resolver = new StubDnsResolver(addresses);
        await using var provider = Build(resolver, allowInsecure: false);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));

        // Port 9 has no listener: had the callback dialled, the failure would be a socket error,
        // not the refusal. The mixed case would dial 192.0.2.10 first and hang until the bound.
        var act = async () =>
        {
            using var response = await client.GetAsync(new Uri("https://internal.example.test:9/hook"), bounded.Token);
        };

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        IsRefusal(thrown.Which).Should().BeTrue(
            "the callback refuses before connecting when ANY resolved address is not public; got {0}", thrown.Which);
        resolver.ResolvedHosts.Should().Contain("internal.example.test", "the callback resolves the host itself at connect time");
    }

    [Fact]
    public async Task InsecureAllowed_SkipsCheck()
    {
        // The one loopback socket in this file: with the check skipped the callback connects to the
        // first resolved address, and only a listener can observe that. Nothing leaves the machine.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var resolver = new StubDnsResolver("127.0.0.1");
        await using var provider = Build(resolver, allowInsecure: true);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));

        var answered = AnswerOneRequestAsync(listener, bounded.Token);
        using var response = await client.GetAsync(new UriBuilder("http", "hook.test", port, "/in").Uri, bounded.Token);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await answered).Should().BeTrue("the connection reached the loopback address the resolver returned first");
        resolver.ResolvedHosts.Should().Contain("hook.test");
    }

    [Fact]
    public async Task DevelopmentWithoutFlag_StillRefusesLoopback()
    {
        var resolver = new StubDnsResolver("127.0.0.1");
        await using var provider = Build(resolver, allowInsecure: false, environmentName: Environments.Development);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        var act = async () =>
        {
            using var response = await client.GetAsync(new Uri("http://hook.test:9/in"), Ct);
        };

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        IsRefusal(thrown.Which).Should().BeTrue("Development alone does not relax the connect-time check");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool IsRefusal(Exception exception) =>
        exception is WebhookTargetRefusedException || exception.InnerException is WebhookTargetRefusedException;

    private static ServiceProvider Build(StubDnsResolver resolver, bool allowInsecure, string? environmentName = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Webhooks:Enabled"] = "true",
                ["Webhooks:AllowInsecureTargetsInDevelopment"] = allowInsecure ? "true" : "false",
            })
            .Build();

        var environment = environmentName ?? (allowInsecure ? Environments.Development : Environments.Production);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environment));
        services.AddSingleton<IWebhookDnsResolver>(resolver);
        services.AddWebhooks(configuration);
        return services.BuildServiceProvider();
    }

    private static async Task<bool> AnswerOneRequestAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = connection.GetStream();
        var buffer = new byte[4096];
        var received = new StringBuilder();
        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return false;
            }

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        await stream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return true;
    }

    /// <summary>Answers every host with the same fixed addresses and records which hosts were asked.</summary>
    private sealed class StubDnsResolver(params string[] addresses) : IWebhookDnsResolver
    {
        private readonly ConcurrentQueue<string> _hosts = new();

        public IReadOnlyList<string> ResolvedHosts => [.. _hosts];

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
        {
            _hosts.Enqueue(host);
            return Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());
        }
    }
}
