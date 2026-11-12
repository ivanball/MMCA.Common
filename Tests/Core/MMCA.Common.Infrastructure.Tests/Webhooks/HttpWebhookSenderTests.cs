using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Webhooks.Delivery;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Infrastructure.Webhooks;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Tests.Webhooks;

/// <summary>
/// <c>HttpWebhookSender</c> (design 35.31 to 35.35), resolved as the <see cref="IWebhookSender"/> that
/// <c>AddWebhooks</c> registers, with the <c>MMCA.Webhooks</c> client's primary handler replaced by a
/// stub so nothing leaves the process. The request is a Standard Webhooks POST signed with every secret
/// it is handed, the outcome is mapped from the status code or the exception alone, and the response
/// body is never read.
/// </summary>
public sealed class HttpWebhookSenderTests
{
    private const string ClientName = "MMCA.Webhooks";
    private const string ProtectedPrefix = "protected:";
    private const string Body = """{"type":"ticket.opened","timestamp":"2026-11-12T09:30:00.0000000Z","data":{"id":7}}""";

    private static readonly Uri Target = new("https://hooks.example.com/in");
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 30, 0, TimeSpan.Zero);
    private static readonly string DeliveryId = new Guid(0x6f1c2b8e, 0x3d4a, 0x4b5c, 0x9e, 0x6f, 0x7a, 0x8b, 0x9c, 0xd, 0x1e, 0x2f) /* 6f1c2b8e-3d4a-4b5c-9e6f-7a8b9c0d1e2f */.ToString("D");

    [Fact]
    public async Task SendsPostWithAllHeaders()
    {
        using var harness = new SenderHarness(_ => Respond(HttpStatusCode.OK));
        var secret = WebhookSignature.GenerateSecret();

        var outcome = await harness.Sender.SendAsync(Request(secret), Ct);

        outcome.Status.Should().Be(WebhookSendStatus.Delivered);
        outcome.HttpStatusCode.Should().Be(200);
        var sent = harness.Handler.Requests.Should().ContainSingle().Which;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Uri.Should().Be(Target);
        sent.Header(WebhookSignature.IdHeader).Should().Be(DeliveryId);
        sent.Header(WebhookSignature.TimestampHeader).Should().Be(
            Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), "the timestamp is Unix seconds from the TimeProvider at send");
        sent.Headers[WebhookSignature.SignatureHeader].Should().ContainSingle("the signatures travel space-separated in ONE header value")
            .Which.Should().StartWith("v1,");
        sent.Header("User-Agent").Should().Be("MMCA-Webhooks/1");
        sent.ContentMediaType.Should().Be("application/json");
        sent.SentBody.Should().Be(Body, "the envelope is sent byte for byte as it was built");
    }

    [Fact]
    public async Task SignatureVerifiesWithReturnedSecret()
    {
        using var harness = new SenderHarness(_ => Respond(HttpStatusCode.NoContent));
        var secret = WebhookSignature.GenerateSecret();

        await harness.Sender.SendAsync(Request(secret), Ct);

        var sent = harness.Handler.Requests.Single();
        var timestamp = long.Parse(sent.Header(WebhookSignature.TimestampHeader), CultureInfo.InvariantCulture);
        var signature = sent.Header(WebhookSignature.SignatureHeader);
        var key = IndependentKey(secret);
        signature.Should().Be(IndependentSignature(DeliveryId, timestamp, Body, key), "v1 is Base64(HMACSHA256(key, id.timestamp.body))");
        WebhookSignature.Verify(DeliveryId, timestamp, Body, signature, key, Now.ToUnixTimeSeconds(), TimeSpan.FromMinutes(5))
            .Should().BeTrue("a receiver holding the plaintext secret from create verifies the delivery");
    }

    [Fact]
    public async Task TwoSignaturesDuringGrace()
    {
        using var harness = new SenderHarness(_ => Respond(HttpStatusCode.OK));
        var current = WebhookSignature.GenerateSecret();
        var previous = WebhookSignature.GenerateSecret();

        await harness.Sender.SendAsync(Request(current, previous), Ct);

        var sent = harness.Handler.Requests.Single();
        var timestamp = long.Parse(sent.Header(WebhookSignature.TimestampHeader), CultureInfo.InvariantCulture);
        var entries = sent.Header(WebhookSignature.SignatureHeader).Split(' ');
        entries.Should().Equal(
            IndependentSignature(DeliveryId, timestamp, Body, IndependentKey(current)),
            IndependentSignature(DeliveryId, timestamp, Body, IndependentKey(previous)));
        WebhookSignature.Verify(DeliveryId, timestamp, Body, sent.Header(WebhookSignature.SignatureHeader), IndependentKey(previous), Now.ToUnixTimeSeconds(), TimeSpan.FromMinutes(5))
            .Should().BeTrue("a receiver still on the previous secret keeps verifying during the grace window");
    }

    [Fact]
    public async Task Redirect_NotFollowed_IsRejected()
    {
        using var harness = new SenderHarness(_ =>
        {
            var response = Respond(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://elsewhere.example.com/in");
            return response;
        });

        var outcome = await harness.Sender.SendAsync(Request(WebhookSignature.GenerateSecret()), Ct);

        outcome.Status.Should().Be(WebhookSendStatus.Rejected, "a redirect is a refusal by the receiver, never followed");
        outcome.HttpStatusCode.Should().Be(302);
        harness.Handler.Requests.Should().ContainSingle("the Location is not requested");
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(204)]
    public async Task Any2xx_IsDelivered(int status)
    {
        using var harness = new SenderHarness(_ => Respond((HttpStatusCode)status));

        var outcome = await harness.Sender.SendAsync(Request(WebhookSignature.GenerateSecret()), Ct);

        outcome.Status.Should().Be(WebhookSendStatus.Delivered);
        outcome.HttpStatusCode.Should().Be(status);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(307)]
    [InlineData(308)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task OtherStatus_IsRejected(int status)
    {
        using var harness = new SenderHarness(_ => Respond((HttpStatusCode)status));

        var outcome = await harness.Sender.SendAsync(Request(WebhookSignature.GenerateSecret()), Ct);

        outcome.Status.Should().Be(WebhookSendStatus.Rejected);
        outcome.HttpStatusCode.Should().Be(status);
    }

    [Fact]
    public async Task Http410_IsGone()
    {
        using var harness = new SenderHarness(_ => Respond(HttpStatusCode.Gone));

        var outcome = await harness.Sender.SendAsync(Request(WebhookSignature.GenerateSecret()), Ct);

        outcome.Status.Should().Be(WebhookSendStatus.Gone);
        outcome.HttpStatusCode.Should().Be(410);
    }

    [Fact]
    public async Task Timeout_IsTransportFailed()
    {
        // The shape HttpClient surfaces when its own Timeout elapses: a TaskCanceledException over a
        // TimeoutException while the caller's token is still live.
        using var harness = new SenderHarness(_ => throw new TaskCanceledException("The request timed out.", new TimeoutException()));

        var outcome = await harness.Sender.SendAsync(Request(WebhookSignature.GenerateSecret()), Ct);

        outcome.Status.Should().Be(WebhookSendStatus.TransportFailed, "a timeout is retried by the queue, not recorded as a refusal");
        outcome.HttpStatusCode.Should().BeNull();
    }

    [Fact]
    public async Task ConnectionError_IsTransportFailed()
    {
        using var harness = new SenderHarness(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused."));

        var outcome = await harness.Sender.SendAsync(Request(WebhookSignature.GenerateSecret()), Ct);

        outcome.Status.Should().Be(WebhookSendStatus.TransportFailed);
        outcome.HttpStatusCode.Should().BeNull();
        outcome.Detail.Should().NotBeNullOrWhiteSpace("the reason feeds RecordFailed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedException_IsTargetRefused(bool wrappedByTheHandler)
    {
        // SocketsHttpHandler surfaces a ConnectCallback exception as the inner exception of an
        // HttpRequestException; both shapes must map the same way.
        using var harness = new SenderHarness(_ => throw (wrappedByTheHandler
            ? new HttpRequestException("Connect failed.", new WebhookTargetRefusedException("hooks.example.com resolved to 10.0.0.7.")) as Exception
            : new WebhookTargetRefusedException("hooks.example.com resolved to 10.0.0.7.")));

        var outcome = await harness.Sender.SendAsync(Request(WebhookSignature.GenerateSecret()), Ct);

        outcome.Status.Should().Be(WebhookSendStatus.TargetRefused);
        outcome.HttpStatusCode.Should().BeNull();
    }

    [Fact]
    public async Task UnreadableSecret_IsSecretUnreadable()
    {
        using var harness = new SenderHarness(_ => Respond(HttpStatusCode.OK));
        var request = new WebhookDeliveryRequest(Target, DeliveryId, Body, ["written-under-a-lost-key-ring"]);

        var outcome = await harness.Sender.SendAsync(request, Ct);

        outcome.Status.Should().Be(WebhookSendStatus.SecretUnreadable);
        harness.Handler.Requests.Should().BeEmpty("an unsigned delivery is never sent");
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var harness = new SenderHarness(async (_, token) =>
        {
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            return Respond(HttpStatusCode.OK);
        });

        var act = () => harness.Sender.SendAsync(Request(WebhookSignature.GenerateSecret()), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("host shutdown cancellation propagates and is not an outcome (rule 35.35)");
    }

    [Fact]
    public async Task ResponseBody_IsNeverReadAndIsDisposed()
    {
        var body = new TrackingStream();
        using var harness = new SenderHarness(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });

        var outcome = await harness.Sender.SendAsync(Request(WebhookSignature.GenerateSecret()), Ct);

        outcome.Status.Should().Be(WebhookSendStatus.Delivered);
        body.Reads.Should().Be(0, "the sender asks for ResponseHeadersRead and never reads the body");
        body.Disposed.Should().BeTrue("the response is disposed");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebhookDeliveryRequest Request(params string[] plainSecrets) =>
        new(Target, DeliveryId, Body, [.. plainSecrets.Select(s => ProtectedPrefix + s)]);

    private static HttpResponseMessage Respond(HttpStatusCode status) => new(status) { Content = new StringContent(string.Empty) };

    private static byte[] IndependentKey(string secret) =>
        Convert.FromBase64String(secret[WebhookSignature.SecretPrefix.Length..]);

    private static string IndependentSignature(string id, long timestamp, string body, byte[] key) =>
        "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{id}.{timestamp}.{body}"))));

    /// <summary>The DI graph AddWebhooks builds, with a fixed clock, a reversible protector and a stub primary handler.</summary>
    private sealed class SenderHarness : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;

        public SenderHarness(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        public SenderHarness(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            Handler = new StubHandler(respond);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Webhooks:Enabled"] = "true" })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
            services.AddSingleton<IWebhookSecretProtector>(new PrefixProtector());
            services.AddWebhooks(configuration);

            // Registered after AddWebhooks, so this primary handler replaces the SocketsHttpHandler.
            services.AddHttpClient(ClientName).ConfigurePrimaryHttpMessageHandler(() => Handler);
            _provider = services.BuildServiceProvider();
            _scope = _provider.CreateScope();
            Sender = _scope.ServiceProvider.GetRequiredService<IWebhookSender>();
        }

        public StubHandler Handler { get; }

        public IWebhookSender Sender { get; }

        public void Dispose()
        {
            _scope.Dispose();
            _provider.Dispose();
        }
    }

    /// <summary>Reverses a fixed prefix; anything without it reads as written under a lost key ring.</summary>
    private sealed class PrefixProtector : IWebhookSecretProtector
    {
        public string Protect(string secret) => ProtectedPrefix + secret;

        public Result<string> Unprotect(string protectedSecret) =>
            protectedSecret.StartsWith(ProtectedPrefix, StringComparison.Ordinal)
                ? Result.Success(protectedSecret[ProtectedPrefix.Length..])
                : Result.Failure<string>(Error.Validation("Test.Unreadable", "The protected secret cannot be read."));
    }

    /// <summary>What the handler saw of one request, captured before the request is disposed.</summary>
    private sealed record SentRequest(
        HttpMethod Method,
        Uri? Uri,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Headers,
        string? ContentMediaType,
        string SentBody)
    {
        public string Header(string name) => Headers.TryGetValue(name, out var values) ? string.Join(",", values) : string.Empty;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<SentRequest> _requests = new();

        public IReadOnlyList<SentRequest> Requests => [.. _requests];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                headers[header.Key] = [.. header.Value];
            }

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            _requests.Enqueue(new SentRequest(request.Method, request.RequestUri, headers, request.Content?.Headers.ContentType?.MediaType, body));
            return await respond(request, cancellationToken);
        }
    }

    /// <summary>A response body that counts every read and records its disposal.</summary>
    private sealed class TrackingStream : Stream
    {
        public int Reads { get; private set; }

        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return 0;
        }

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(0);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult(0);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
