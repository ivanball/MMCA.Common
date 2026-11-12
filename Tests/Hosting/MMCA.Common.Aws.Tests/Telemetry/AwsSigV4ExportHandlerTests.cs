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
public sealed partial class AwsSigV4ExportHandlerTests
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

        using var response = await SendOnPathAsync(invoker, request, synchronous);

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

        using var response = await SendOnPathAsync(invoker, request, synchronous);

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

        using var response = await SendOnPathAsync(invoker, request, synchronous);

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

        using var firstResponse = await SendOnPathAsync(invoker, first, synchronous);
        using var secondResponse = await SendOnPathAsync(invoker, second, synchronous);

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

        using var response = await SendOnPathAsync(invoker, request, synchronous);

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

        using var response = await SendOnPathAsync(invoker, request, synchronous);

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

        using var response = await SendOnPathAsync(invoker, request, synchronous);

        var sent = inner.Requests.Should().ContainSingle().Which;
        sent.Body.Should().Equal(body);
        sent.ContentType.Should().Be("application/x-protobuf");
    }

    // Rulings M1 and M2: the synchronous path resolves the credential itself rather than refusing an
    // export while a lookup is pending, so the first batch after a cold start is signed.
    [Fact]
    public async Task TheFirstSyncExport_IsSigned_WhenTheChainIsSlowButSucceeds()
    {
        var inner = new RecordingHandler();
        var asyncLookup = new TaskCompletionSource<AWSCredentials?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DefaultChainAwsTelemetryCredentialSource(
            () =>
            {
                // A slow chain: the synchronous resolver takes a while, and the asynchronous lookup a
                // warm-up may have started has not finished either.
                using var slow = new ManualResetEventSlim(initialState: false);
                slow.Wait(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
                return new BasicAWSCredentials(AccessKeyId, SecretAccessKey);
            },
            () => asyncLookup.Task);
        var warmup = source.GetAsync(TestContext.Current.CancellationToken).AsTask();
        using var invoker = Invoker(inner, source, "xray");
        using var request = ExportRequest(TracesUri, [1, 2, 3]);

        using var response = await SendOnPathAsync(invoker, request, synchronous: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Requests.Should().ContainSingle().Which.Authorization.Should().StartWith(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261111/us-east-1/xray/aws4_request");
        asyncLookup.SetResult(null);
        await warmup;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AThrowingChain_LogsTheRealException_AndIsRetriedOnTheNextExport(bool synchronous)
    {
        var inner = new RecordingHandler();
        var logger = new RecordingLogger();
        var attempts = 0;
        var source = new FakeCredentialSource(() => Interlocked.Increment(ref attempts) == 1
            ? throw new AmazonClientException("the instance metadata service did not answer")
            : new BasicAWSCredentials(AccessKeyId, SecretAccessKey));
        using var invoker = Invoker(inner, source, "xray", logger);
        using var first = ExportRequest(TracesUri, [1, 2, 3]);
        using var second = ExportRequest(TracesUri, [4, 5, 6]);

        using var firstResponse = await SendOnPathAsync(invoker, first, synchronous);
        using var secondResponse = await SendOnPathAsync(invoker, second, synchronous);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK, "a failed lookup is not cached, so the next export tries again");
        inner.Requests.Should().ContainSingle("only the second export had a credential to sign with");
        source.Calls.Should().Be(2);
        var warning = logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning).Which;
        warning.Message.Should().Contain(nameof(AmazonClientException))
            .And.Contain("the instance metadata service did not answer");
        warning.Exception.Should().BeOfType<AmazonClientException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AThrowingSigner_SendsNothing_AndAnswers401(bool synchronous)
    {
        var inner = new RecordingHandler();
        var logger = new RecordingLogger();
        using var invoker = Invoker(inner, new StaticAwsTelemetryCredentialSource(new ThrowingCredentials()), "xray", logger);
        using var request = ExportRequest(TracesUri, [1, 2, 3]);

        using var response = await SendOnPathAsync(invoker, request, synchronous);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        inner.Requests.Should().BeEmpty("a request the signer could not sign must not leave the process");
        var warning = logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning).Which;
        warning.Message.Should().Contain(nameof(AmazonClientException)).And.Contain("the credential refresh failed");
    }

    [Fact]
    public async Task NoLogLine_CarriesTheKeyIdTheSecretOrASignature()
    {
        const string distinctSecret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
        var inner = new RecordingHandler();
        var logger = new RecordingLogger();
        var attempts = 0;

        // Every outcome the handler logs, on both paths: a throwing lookup, a null lookup, a throwing
        // signer, and successful signatures in between.
        var source = new FakeCredentialSource(() => (Interlocked.Increment(ref attempts) % 3) switch
        {
            1 => throw new AmazonClientException("no credential in the chain"),
            2 => null,
            _ => new BasicAWSCredentials(AccessKeyId, distinctSecret),
        });
        using var invoker = Invoker(inner, source, "logs", logger);
        using var throwingSigner = Invoker(inner, new StaticAwsTelemetryCredentialSource(new ThrowingCredentials()), "logs", logger);
        foreach (var synchronous in new[] { true, false, true, false, true, false })
        {
            using var request = ExportRequest(LogsUri, [1, 2, 3]);
            using var response = await SendOnPathAsync(invoker, request, synchronous);
            using var refused = ExportRequest(LogsUri, [4, 5, 6]);
            using var refusedResponse = await SendOnPathAsync(throwingSigner, refused, synchronous);
        }

        inner.Requests.Should().NotBeEmpty();
        logger.Entries.Should().NotBeEmpty();
        var signatures = inner.Requests.Select(sent => Signature(sent.Authorization)).ToList();
        foreach (var entry in logger.Entries)
        {
            entry.Text.Should().NotContain(AccessKeyId).And.NotContain(distinctSecret);
            foreach (var signature in signatures)
            {
                entry.Text.Should().NotContain(signature);
            }
        }
    }

    // Ruling L8: the signature covers a fixed, minimal header set, never whatever else the request
    // carries, so a multi-valued header joined differently from the wire cannot break it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SignedHeaders_AreExactlyTheFixedSet_ForTraces(bool synchronous)
    {
        var inner = new RecordingHandler();
        using var invoker = Invoker(inner, StaticCredentials(), "xray");
        using var request = WithExtraHeaders(ExportRequest(TracesUri, [1, 2, 3]));

        using var response = await SendOnPathAsync(invoker, request, synchronous);

        SignedHeaders(inner.Requests.Should().ContainSingle().Which.Authorization)
            .Should().BeEquivalentTo("content-type", "host", "x-amz-content-sha256", "x-amz-date");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SignedHeaders_AreExactlyTheFixedSet_ForLogs(bool synchronous)
    {
        var inner = new RecordingHandler();
        using var invoker = new HttpMessageInvoker(new AwsSigV4ExportHandler(
            StaticCredentials(),
            RegionEndpoint.USEast1,
            "logs",
            new FakeTimeProvider(SigningTime),
            NullLogger.Instance,
            [new("x-aws-log-group", "mmca-group"), new("x-aws-log-stream", "mmca-stream")])
        {
            InnerHandler = inner,
        });
        using var request = WithExtraHeaders(ExportRequest(LogsUri, [1, 2, 3]));

        using var response = await SendOnPathAsync(invoker, request, synchronous);

        var sent = inner.Requests.Should().ContainSingle().Which;
        SignedHeaders(sent.Authorization).Should().BeEquivalentTo("content-type", "host", "x-amz-content-sha256", "x-amz-date", "x-aws-log-group", "x-aws-log-stream");
        sent.Header("x-aws-log-group").Should().Be("mmca-group");
        sent.Header("x-aws-log-stream").Should().Be("mmca-stream");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultiValuedHeaders_DoNotChangeTheSignature(bool synchronous)
    {
        var inner = new RecordingHandler();
        using var invoker = Invoker(inner, StaticCredentials(), "xray");
        using var plain = ExportRequest(TracesUri, [1, 2, 3]);
        using var decorated = WithExtraHeaders(ExportRequest(TracesUri, [1, 2, 3]));

        using var plainResponse = await SendOnPathAsync(invoker, plain, synchronous);
        using var decoratedResponse = await SendOnPathAsync(invoker, decorated, synchronous);

        inner.Requests.Should().HaveCount(2);
        Signature(inner.Requests[1].Authorization).Should().Be(
            Signature(inner.Requests[0].Authorization),
            "the signature depends only on the fixed header set, the URL, the clock and the body");
        inner.Requests[1].Header("User-Agent").Should().NotBeNullOrEmpty("unsigned headers still go out on the wire");
    }

    private static HttpRequestMessage WithExtraHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd("OTel-OTLP-Exporter-Dotnet/1.15.0");
        request.Headers.UserAgent.ParseAdd("mmca-test/1.0");
        request.Headers.Accept.ParseAdd("application/x-protobuf");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("x-custom", ["one", "two"]);
        return request;
    }

    private static StaticAwsTelemetryCredentialSource StaticCredentials() =>
        new(new BasicAWSCredentials(AccessKeyId, SecretAccessKey));

    private static HttpMessageInvoker Invoker(HttpMessageHandler inner, IAwsTelemetryCredentialSource credentials, string service, ILogger? logger = null) =>
        new(new AwsSigV4ExportHandler(
            credentials, RegionEndpoint.USEast1, service, new FakeTimeProvider(SigningTime), logger ?? NullLogger.Instance)
        {
            InnerHandler = inner,
        });

    private static HttpRequestMessage ExportRequest(string uri, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        return new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
    }

    private static Task<HttpResponseMessage> SendOnPathAsync(HttpMessageInvoker invoker, HttpRequestMessage request, bool synchronous) =>
        synchronous
            ? Task.FromResult(SendSynchronously(invoker, request))
            : invoker.SendAsync(request, TestContext.Current.CancellationToken);

    // The exporter's own path, HttpMessageInvoker.Send, kept out of the Task-returning helper.
    private static HttpResponseMessage SendSynchronously(HttpMessageInvoker invoker, HttpRequestMessage request) =>
        invoker.Send(request, TestContext.Current.CancellationToken);

    private static string[] SignedHeaders(string authorization)
    {
        var match = SignedHeadersPattern.Match(authorization);
        match.Success.Should().BeTrue($"'{authorization}' carries SignedHeaders");
        return match.Groups["headers"].Value.Split(';');
    }

    private static string Signature(string authorization)
    {
        var match = SignaturePattern.Match(authorization);
        match.Success.Should().BeTrue($"'{authorization}' carries a Signature");
        return match.Groups["signature"].Value;
    }

    [GeneratedRegex("SignedHeaders=(?<headers>[^,\\s]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SignedHeadersPattern { get; }

    [GeneratedRegex("Signature=(?<signature>[0-9a-f]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SignaturePattern { get; }

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

    /// <summary>Answers both paths from one delegate, counting the calls.</summary>
    private sealed class FakeCredentialSource(Func<AWSCredentials?> resolve) : IAwsTelemetryCredentialSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<AWSCredentials?> GetAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            try
            {
                return ValueTask.FromResult(resolve());
            }
            catch (Exception ex)
            {
                return ValueTask.FromException<AWSCredentials?>(ex);
            }
        }

        public AWSCredentials? Get(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return resolve();
        }
    }

    /// <summary>Credentials whose resolution throws inside the SDK signer, as a failed refresh does.</summary>
    private sealed class ThrowingCredentials : AWSCredentials
    {
        public override ImmutableCredentials GetCredentials() =>
            throw new AmazonClientException("the credential refresh failed");
    }

    /// <summary>One log entry as written: level, rendered message and exception.</summary>
    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception)
    {
        public string Text => Message + Environment.NewLine + Exception;
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IReadOnlyList<LogLevel> Levels => [.. Entries.Select(static entry => entry.Level)];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }
    }
}
