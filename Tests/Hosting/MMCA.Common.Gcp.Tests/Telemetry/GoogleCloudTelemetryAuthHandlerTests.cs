using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Gcp.Telemetry;

namespace MMCA.Common.Gcp.Tests.Telemetry;

/// <summary>
/// The exporter's <c>DelegatingHandler</c>: every request, on the synchronous path the OTLP/HTTP
/// exporter uses and on the asynchronous one, carries the bearer token from the injected token
/// source and the quota project; with no token yet it refuses the request locally instead of sending
/// it unauthenticated. The access token refreshes from the source and keeps the last good token
/// through a failed refresh.
/// </summary>
public sealed class GoogleCloudTelemetryAuthHandlerTests
{
    private const string ProjectId = "mmca-prod";

    [Fact]
    public async Task Send_AddsTheBearerTokenAndTheQuotaProject()
    {
        using var accessToken = await RefreshedAccessTokenAsync("token-1");
        var inner = new RecordingHandler();
        using var invoker = new HttpMessageInvoker(new GoogleCloudTelemetryAuthHandler(accessToken, ProjectId) { InnerHandler = inner });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://telemetry.googleapis.com/v1/traces");

        using var response = SendSynchronously(invoker, request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Authorization.Should().Be("Bearer token-1");
        inner.QuotaProject.Should().Be(ProjectId);
    }

    [Fact]
    public async Task SendAsync_AddsTheBearerTokenAndTheQuotaProject()
    {
        using var accessToken = await RefreshedAccessTokenAsync("token-2");
        var inner = new RecordingHandler();
        using var invoker = new HttpMessageInvoker(new GoogleCloudTelemetryAuthHandler(accessToken, ProjectId) { InnerHandler = inner });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://telemetry.googleapis.com/v1/logs");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Authorization.Should().Be("Bearer token-2");
        inner.QuotaProject.Should().Be(ProjectId);
    }

    [Fact]
    public async Task EveryRequest_ReadsTheCurrentToken()
    {
        var source = new SwitchableTokenSource("first");
        using var accessToken = new GoogleCloudTelemetryAccessToken(source, NullLogger<GoogleCloudTelemetryAccessToken>.Instance);
        await accessToken.RefreshAsync(CancellationToken.None);
        var inner = new RecordingHandler();
        using var invoker = new HttpMessageInvoker(new GoogleCloudTelemetryAuthHandler(accessToken, ProjectId) { InnerHandler = inner });

        using (var first = new HttpRequestMessage(HttpMethod.Post, "https://telemetry.googleapis.com/v1/traces"))
        {
            using var response = SendSynchronously(invoker, first);
        }

        inner.Authorization.Should().Be("Bearer first");

        source.Token = "second";
        await accessToken.RefreshAsync(CancellationToken.None);
        using (var second = new HttpRequestMessage(HttpMethod.Post, "https://telemetry.googleapis.com/v1/traces"))
        {
            using var response = SendSynchronously(invoker, second);
        }

        inner.Authorization.Should().Be("Bearer second");
    }

    [Fact]
    public async Task WithoutAToken_RefusesTheRequest_AndSendsNothing()
    {
        using var accessToken = new GoogleCloudTelemetryAccessToken(
            new FakeTelemetryTokenSource(null), NullLogger<GoogleCloudTelemetryAccessToken>.Instance);
        var inner = new RecordingHandler();
        using var invoker = new HttpMessageInvoker(new GoogleCloudTelemetryAuthHandler(accessToken, ProjectId) { InnerHandler = inner });
        using var syncRequest = new HttpRequestMessage(HttpMethod.Post, "https://telemetry.googleapis.com/v1/traces");
        using var asyncRequest = new HttpRequestMessage(HttpMethod.Post, "https://telemetry.googleapis.com/v1/logs");

        var send = () => SendSynchronously(invoker, syncRequest);
        var sendAsync = () => invoker.SendAsync(asyncRequest, CancellationToken.None);

        send.Should().Throw<HttpRequestException>().Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await sendAsync.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        inner.Calls.Should().Be(0, "an unauthenticated request must never leave the process");
    }

    [Fact]
    public async Task AFailedRefresh_KeepsTheLastGoodToken()
    {
        var source = new SwitchableTokenSource("good");
        using var accessToken = new GoogleCloudTelemetryAccessToken(source, NullLogger<GoogleCloudTelemetryAccessToken>.Instance);
        (await accessToken.RefreshAsync(CancellationToken.None)).Should().BeTrue();

        source.Token = null;
        var refreshed = await accessToken.RefreshAsync(CancellationToken.None);

        refreshed.Should().BeFalse();
        accessToken.Current.Should().Be("good");
    }

    [Fact]
    public async Task StartAsync_FetchesTheFirstToken_AndSurvivesMissingCredentials()
    {
        using var withCredentials = new GoogleCloudTelemetryAccessToken(
            new FakeTelemetryTokenSource("start-token"), NullLogger<GoogleCloudTelemetryAccessToken>.Instance);
        await withCredentials.StartAsync(CancellationToken.None);
        withCredentials.Current.Should().Be("start-token");
        await withCredentials.StopAsync(CancellationToken.None);

        // No Application Default Credentials must not fail the host: telemetry is not worth an outage.
        using var withoutCredentials = new GoogleCloudTelemetryAccessToken(
            new FakeTelemetryTokenSource(null), NullLogger<GoogleCloudTelemetryAccessToken>.Instance);
        var start = () => withoutCredentials.StartAsync(CancellationToken.None);
        await start.Should().NotThrowAsync();
        withoutCredentials.Current.Should().BeNull();
        await withoutCredentials.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// The synchronous send path, deliberately: it is the one the OTLP/HTTP exporter uses on .NET,
    /// so it is the path that has to carry the headers.
    /// </summary>
    private static HttpResponseMessage SendSynchronously(HttpMessageInvoker invoker, HttpRequestMessage request) =>
        invoker.Send(request, CancellationToken.None);

    private static async Task<GoogleCloudTelemetryAccessToken> RefreshedAccessTokenAsync(string token)
    {
        var accessToken = new GoogleCloudTelemetryAccessToken(
            new FakeTelemetryTokenSource(token), NullLogger<GoogleCloudTelemetryAccessToken>.Instance);
        await accessToken.RefreshAsync(CancellationToken.None);
        return accessToken;
    }

    private sealed class SwitchableTokenSource(string? token) : IGoogleCloudTelemetryTokenSource
    {
        public string? Token { get; set; } = token;

        public Task<string> GetTokenAsync(CancellationToken cancellationToken) =>
            Token is null
                ? Task.FromException<string>(new InvalidOperationException("Refresh failed."))
                : Task.FromResult(Token);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public string? Authorization { get; private set; }

        public string? QuotaProject { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Authorization = request.Headers.Authorization?.ToString();
            QuotaProject = request.Headers.TryGetValues("x-goog-user-project", out var values) ? values.Single() : null;
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }
}
