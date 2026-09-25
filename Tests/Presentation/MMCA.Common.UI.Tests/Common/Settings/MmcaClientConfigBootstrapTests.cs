using System.Net;
using System.Text;
using AwesomeAssertions;
using MMCA.Common.UI.Common.Settings;

namespace MMCA.Common.UI.Tests.Common.Settings;

/// <summary>
/// Tests for <see cref="MmcaClientConfigBootstrap"/>: the WASM boot fetch of <c>client-config</c>
/// returns the document buffered, retries exactly once on a transport failure or a timeout, fails
/// loudly on a second failure, and never retries a fetch the caller cancelled.
/// </summary>
public sealed class MmcaClientConfigBootstrapTests
{
    private const string Document = """{"api":{"apiEndpoint":"https://gateway.example.com"}}""";

    [Fact]
    public async Task LoadAsync_ReturnsTheDocumentFromTheFirstAttempt()
    {
        var handler = new ScriptedHandler(_ => Ok());
        using var client = CreateClient(handler);

        await using var stream = await MmcaClientConfigBootstrap.LoadAsync(client, TimeSpan.Zero, TestContext.Current.CancellationToken);

        (await ReadAsync(stream)).Should().Be(Document);
        handler.Calls.Should().Be(1);
        handler.LastUri!.AbsolutePath.Should().Be("/client-config");
    }

    [Fact]
    public async Task LoadAsync_RetriesOnceAfterATransportFailure()
    {
        var handler = new ScriptedHandler(call => call == 1 ? throw new HttpRequestException("cold start") : Ok());
        using var client = CreateClient(handler);

        await using var stream = await MmcaClientConfigBootstrap.LoadAsync(client, TimeSpan.Zero, TestContext.Current.CancellationToken);

        (await ReadAsync(stream)).Should().Be(Document);
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task LoadAsync_RetriesOnceAfterAServerError()
    {
        var handler = new ScriptedHandler(call => call == 1 ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Ok());
        using var client = CreateClient(handler);

        await using var stream = await MmcaClientConfigBootstrap.LoadAsync(client, TimeSpan.Zero, TestContext.Current.CancellationToken);

        (await ReadAsync(stream)).Should().Be(Document);
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task LoadAsync_RetriesOnceAfterATimeout()
    {
        var handler = new ScriptedHandler(call => call == 1 ? throw new TaskCanceledException("timed out") : Ok());
        using var client = CreateClient(handler);

        await using var stream = await MmcaClientConfigBootstrap.LoadAsync(client, TimeSpan.Zero, TestContext.Current.CancellationToken);

        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task LoadAsync_WhenBothAttemptsFail_Throws()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = CreateClient(handler);

        var act = () => MmcaClientConfigBootstrap.LoadAsync(client, TimeSpan.Zero, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task LoadAsync_WhenTheCallerCancels_DoesNotRetry()
    {
        using var cts = new CancellationTokenSource();
        var handler = new ScriptedHandler(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException("cancelled");
        });
        using var client = CreateClient(handler);

        var act = () => MmcaClientConfigBootstrap.LoadAsync(client, TimeSpan.Zero, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public void DefaultTimeout_BoundsTheBootWellUnderTheHttpClientDefault() =>
        MmcaClientConfigBootstrap.DefaultTimeout.Should().Be(TimeSpan.FromSeconds(15));

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK) { Content = new StringContent(Document, Encoding.UTF8, "application/json") };

    private static HttpClient CreateClient(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://app.example.com/") };

    private static async Task<string> ReadAsync(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private sealed class ScriptedHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(respond(Calls));
        }
    }
}
