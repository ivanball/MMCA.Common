using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Amazon;
using Amazon.Runtime;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Aws.Telemetry;

namespace MMCA.Common.Aws.Tests.Telemetry;

/// <summary>
/// Item 32: the exporter's <c>DelegatingHandler</c> signs every OTLP request with AWS Signature
/// Version 4 for the configured service and region, over the buffered body, at the injected clock's
/// time, and refuses (401, nothing sent) when no credential can be resolved. The keys are the
/// documentation example key id with a placeholder secret, so nothing here could authenticate anywhere.
/// <para>
/// Every behavioural fact runs on BOTH send paths. The OTLP/HTTP exporter calls the synchronous
/// <see cref="HttpMessageInvoker.Send"/> on .NET (see the Google twin,
/// <c>MMCA.Common.Gcp/Telemetry/GoogleCloudTelemetryAuthHandler.cs</c>, which overrides both), and a
/// <see cref="DelegatingHandler"/> that overrides only <c>SendAsync</c> forwards the synchronous call to
/// its inner handler unsigned.
/// </para>
/// </summary>
public sealed class AwsSigV4ExportHandlerTests
{
    private const string AccessKeyId = "AKIDEXAMPLE";
    private const string SecretAccessKey = "secret";
    private const string TracesUri = "https://xray.us-east-1.amazonaws.com/v1/traces";
    private const string LogsUri = "https://logs.us-east-1.amazonaws.com/v1/logs";

    // DESIGN-QUESTION: rule 12 describes only the asynchronous path (ReadAsByteArrayAsync, SignAsync).
    // The synchronous Send override these theories demand is implied by rule 13 ("no unsigned request
    // ever leaves the process"), not stated; the handler must sign on both paths.
    private static readonly DateTimeOffset SigningTime = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Authorization_IsSigV4_ForTheServiceAndRegion(bool synchronous)
    {
        var inner = new RecordingHandler();
        using var invoker = Invoker(inner, StaticCredentials(), "xray");
        using var request = ExportRequest(TracesUri, [1, 2, 3]);

        using var response = await SendAsync(invoker, request, synchronous);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var sent = inner.Requests.Should().ContainSingle().Which;
        sent.Authorization.Should().StartWith(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261111/us-east-1/xray/aws4_request");
        sent.Authorization.Should().Contain("SignedHeaders=");
        SignedHeaders(sent.Authorization).Should().Contain(["host", "x-amz-date"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task XAmzDate_IsTheInjectedClock(bool synchronous)
    {
        var inner = new RecordingHandler();
        using var invoker = Invoker(inner, StaticCredentials(), "xray");
        using var request = ExportRequest(TracesUri, [1, 2, 3]);

        using var response = await SendAsync(invoker, request, synchronous);

        inner.Requests.Should().ContainSingle().Which.Header("X-Amz-Date").Should().Be("20261111T120000Z");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SessionCredentials_AddTheSecurityToken(bool synchronous)
    {
        var inner = new RecordingHandler();
        var session = new StaticAwsTelemetryCredentialSource(
            new SessionAWSCredentials(AccessKeyId, SecretAccessKey, "example-session-token"));
        using var invoker = Invoker(inner, session, "xray");
        using var request = ExportRequest(TracesUri, [1, 2, 3]);

        using var response = await SendAsync(invoker, request, synchronous);

        var sent = inner.Requests.Should().ContainSingle().Which;
        sent.Header("X-Amz-Security-Token").Should().Be("example-session-token");
        SignedHeaders(sent.Authorization).Should().Contain("x-amz-security-token");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheBody_IsSigned(bool synchronous)
    {
        var inner = new RecordingHandler();
        using var invoker = Invoker(inner, StaticCredentials(), "xray");
        using var first = ExportRequest(TracesUri, [1, 2, 3]);
        using var second = ExportRequest(TracesUri, [4, 5, 6]);

        using var firstResponse = await SendAsync(invoker, first, synchronous);
        using var secondResponse = await SendAsync(invoker, second, synchronous);

        // Same key, same clock, same URL and headers: only the payload hash differs.
        inner.Requests.Should().HaveCount(2);
        Signature(inner.Requests[0].Authorization).Should().NotBe(Signature(inner.Requests[1].Authorization));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LogHeaders_AreSentAndSigned(bool synchronous)
    {
        var inner = new RecordingHandler();
        using var invoker = Invoker(inner, StaticCredentials(), "logs");
        using var request = ExportRequest(LogsUri, [7, 8, 9]);

        // DESIGN-QUESTION: rule 10 says "the logs handler adds x-aws-log-group and x-aws-log-stream
        // before signing", but the handler constructor in (a) takes no log group or stream. These
        // tests put the two headers on the request (as HttpClient.DefaultRequestHeaders would) and
        // assert the handler signs and forwards them; the end-to-end test in AwsTelemetryExportTests
        // asserts they arrive, whichever way the implementer adds them.
        request.Headers.TryAddWithoutValidation("x-aws-log-group", "mmca-group");
        request.Headers.TryAddWithoutValidation("x-aws-log-stream", "mmca-stream");

        using var response = await SendAsync(invoker, request, synchronous);

        var sent = inner.Requests.Should().ContainSingle().Which;
        sent.Authorization.Should().StartWith(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261111/us-east-1/logs/aws4_request");
        sent.Header("x-aws-log-group").Should().Be("mmca-group");
        sent.Header("x-aws-log-stream").Should().Be("mmca-stream");
        SignedHeaders(sent.Authorization).Should().Contain(["x-aws-log-group", "x-aws-log-stream"]);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task NoCredentials_SendsNothing_AndAnswers401(bool synchronous, bool sourceThrows)
    {
        var inner = new RecordingHandler();
        var logger = new RecordingLogger();
        var source = new FakeCredentialSource(sourceThrows
            ? () => throw new AmazonClientException("no credential in the chain")
            : () => null);
        using var invoker = new HttpMessageInvoker(new AwsSigV4ExportHandler(
            source, RegionEndpoint.USEast1, "xray", new FakeTimeProvider(SigningTime), logger)
        {
            InnerHandler = inner,
        });
        using var request = ExportRequest(TracesUri, [1, 2, 3]);

        using var response = await SendAsync(invoker, request, synchronous);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        inner.Requests.Should().BeEmpty("no unsigned request may leave the process");
        logger.Levels.Should().Contain(LogLevel.Warning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheBodyStillReachesTheInnerHandler(bool synchronous)
    {
        byte[] body = [0x0A, 0x03, 0x66, 0x6F, 0x6F, 0x12, 0x00, 0xFF];
        var inner = new RecordingHandler();
        using var invoker = Invoker(inner, StaticCredentials(), "xray");
        using var request = ExportRequest(TracesUri, body);

        using var response = await SendAsync(invoker, request, synchronous);

        var sent = inner.Requests.Should().ContainSingle().Which;
        sent.Body.Should().Equal(body);
        sent.ContentType.Should().Be("application/x-protobuf");
    }

    private static StaticAwsTelemetryCredentialSource StaticCredentials() =>
        new(new BasicAWSCredentials(AccessKeyId, SecretAccessKey));

    private static HttpMessageInvoker Invoker(HttpMessageHandler inner, IAwsTelemetryCredentialSource credentials, string service) =>
        new(new AwsSigV4ExportHandler(
            credentials, RegionEndpoint.USEast1, service, new FakeTimeProvider(SigningTime), NullLogger.Instance)
        {
            InnerHandler = inner,
        });

    private static HttpRequestMessage ExportRequest(string uri, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        return new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
    }

    private static Task<HttpResponseMessage> SendAsync(HttpMessageInvoker invoker, HttpRequestMessage request, bool synchronous) =>
        synchronous
            ? Task.FromResult(invoker.Send(request, TestContext.Current.CancellationToken))
            : invoker.SendAsync(request, TestContext.Current.CancellationToken);

    private static string[] SignedHeaders(string authorization)
    {
        var match = Regex.Match(authorization, "SignedHeaders=([^,\\s]+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        match.Success.Should().BeTrue($"'{authorization}' carries SignedHeaders");
        return match.Groups[1].Value.Split(';');
    }

    private static string Signature(string authorization)
    {
        var match = Regex.Match(authorization, "Signature=([0-9a-f]+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        match.Success.Should().BeTrue($"'{authorization}' carries a Signature");
        return match.Groups[1].Value;
    }

    /// <summary>One request as the inner handler saw it.</summary>
    private sealed record SentRequest(string Authorization, IReadOnlyDictionary<string, string> Headers, string? ContentType, byte[] Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>Records every request on either send path and answers 200.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = [];
            if (request.Content is not null)
            {
                using var stream = request.Content.ReadAsStream(cancellationToken);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                body = copy.ToArray();
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers.NonValidated)
            {
                headers[header.Key] = header.Value.ToString();
            }

            Requests.Add(new SentRequest(
                request.Headers.NonValidated.TryGetValues("Authorization", out var authorization) ? authorization.ToString() : string.Empty,
                headers,
                request.Content?.Headers.ContentType?.MediaType,
                body));
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class FakeCredentialSource(Func<AWSCredentials?> resolve) : IAwsTelemetryCredentialSource
    {
        public ValueTask<AWSCredentials?> GetAsync(CancellationToken cancellationToken)
        {
            try
            {
                return ValueTask.FromResult(resolve());
            }
            catch (Exception ex)
            {
                return ValueTask.FromException<AWSCredentials?>(ex);
            }
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Levels)
            {
                Levels.Add(logLevel);
            }
        }
    }
}
