using System.Net;
using System.Net.Http.Headers;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Signing;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Aws.Telemetry;

/// <summary>
/// The <see cref="DelegatingHandler"/> on the AWS exporter's <c>HttpClientFactory</c>: every OTLP
/// request is signed with AWS Signature Version 4 for one service (<c>xray</c> or <c>logs</c>) and
/// region, over the buffered body, at the injected clock's time. Both send paths sign, because the
/// OTLP/HTTP exporter uses the synchronous one.
/// <para>
/// When no credential can be resolved the handler sends nothing and answers 401 itself, so no
/// unsigned request ever leaves the process. On the synchronous path a credential lookup that has not
/// finished yet (the default chain's first resolution) counts as "no credential" for that export; the
/// lookup keeps running and the next export signs.
/// </para>
/// </summary>
internal sealed partial class AwsSigV4ExportHandler : DelegatingHandler
{
    private readonly IAwsTelemetryCredentialSource _credentials;

    private readonly RegionEndpoint _region;

    private readonly string _service;

    private readonly TimeProvider _timeProvider;

    private readonly ILogger _logger;

    private readonly KeyValuePair<string, string>[] _addedHeaders;

    private int _warnedNoCredentials;

    private int _warnedCredentialFailure;

    private int _warnedCredentialsPending;

    /// <summary>Initializes a new instance of the <see cref="AwsSigV4ExportHandler"/> class.</summary>
    /// <param name="credentials">The credentials every request is signed with.</param>
    /// <param name="region">The signing region.</param>
    /// <param name="service">The signing service name, <c>xray</c> or <c>logs</c>.</param>
    /// <param name="timeProvider">The clock the signing time is read from.</param>
    /// <param name="logger">Logs one Warning per kind of credential failure.</param>
    /// <param name="addedHeaders">
    /// Headers added to every request BEFORE signing, so they are signed (the logs exporter's
    /// <c>x-aws-log-group</c> and <c>x-aws-log-stream</c>). Each replaces any value already present.
    /// </param>
    public AwsSigV4ExportHandler(
        IAwsTelemetryCredentialSource credentials,
        RegionEndpoint region,
        string service,
        TimeProvider timeProvider,
        ILogger logger,
        IEnumerable<KeyValuePair<string, string>>? addedHeaders = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _credentials = credentials;
        _region = region;
        _service = service;
        _timeProvider = timeProvider;
        _logger = logger;
        _addedHeaders = addedHeaders is null ? [] : [.. addedHeaders];
    }

    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The capture runs synchronously to the end when the lookup has already finished (a static
        // credential, or the default chain once resolved), so no thread is ever blocked here.
        var capture = new CredentialCapture();
        Task captured;
        try
        {
            captured = capture.RunAsync(_credentials.GetAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Warn(ref _warnedCredentialFailure, CredentialFailure.Threw, exception);
            return Refuse(request);
        }

        if (!captured.IsCompleted)
        {
            // Never block the exporter thread on a credential lookup: it finishes in the background
            // (the capture swallows its outcome) and this one export is refused.
            Warn(ref _warnedCredentialsPending, CredentialFailure.Pending, exception: null);
            return Refuse(request);
        }

        if (capture.Failure is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Warn(ref _warnedCredentialFailure, CredentialFailure.Threw, capture.Failure);
            return Refuse(request);
        }

        var credentials = capture.Credentials;
        if (credentials is null)
        {
            Warn(ref _warnedNoCredentials, CredentialFailure.None, exception: null);
            return Refuse(request);
        }

        var body = ReadBody(request, cancellationToken);
        var signing = PrepareSigningRequest(request, body);
        var result = AWSSigV4Signer.Sign(signing, Parameters(credentials));
        ApplySignature(request, result);

        return base.Send(request, cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        AWSCredentials? credentials;
        try
        {
            credentials = await _credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Warn(ref _warnedCredentialFailure, CredentialFailure.Threw, exception);
            return Refuse(request);
        }

        if (credentials is null)
        {
            Warn(ref _warnedNoCredentials, CredentialFailure.None, exception: null);
            return Refuse(request);
        }

        var body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        ReplaceContent(request, body);
        var signing = PrepareSigningRequest(request, body);
        var result = await AWSSigV4Signer.SignAsync(signing, Parameters(credentials), cancellationToken).ConfigureAwait(false);
        ApplySignature(request, result);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The headers the signer computes itself, or that must never be signed from the caller.</summary>
    private static bool IsExcludedFromSigning(string name) =>
        string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("X-Amz-", StringComparison.OrdinalIgnoreCase);

    private static HttpResponseMessage Refuse(HttpRequestMessage request) =>
        new(HttpStatusCode.Unauthorized) { RequestMessage = request };

    /// <summary>Buffers the body on the synchronous path and swaps in a replayable copy.</summary>
    private static byte[]? ReadBody(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null)
        {
            return null;
        }

        using var stream = request.Content.ReadAsStream(cancellationToken);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var body = buffer.ToArray();
        ReplaceContent(request, body);
        return body;
    }

    /// <summary>
    /// Replaces the content with a byte-array copy carrying the same content headers, so the bytes
    /// on the wire are exactly the bytes that were hashed.
    /// </summary>
    private static void ReplaceContent(HttpRequestMessage request, byte[]? body)
    {
        if (request.Content is null || body is null)
        {
            return;
        }

        var original = request.Content;
        var copy = new ByteArrayContent(body);
        foreach (var header in original.Headers.NonValidated.Where(static header =>
                     !string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)))
        {
            copy.Headers.TryAddWithoutValidation(header.Key, [.. header.Value]);
        }

        request.Content = copy;
        original.Dispose();
    }

    private static void ApplySignature(HttpRequestMessage request, AWSSigningResult result)
    {
        foreach (var header in result.Headers.Where(static header =>
                     !string.Equals(header.Key, "host", StringComparison.OrdinalIgnoreCase)))
        {
            request.Headers.Remove(header.Key);
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "AWS telemetry export to {Service} in {Region} was not sent: {Reason}. No unsigned request leaves the process.")]
    private static partial void LogNotSent(ILogger logger, string service, string region, string reason, Exception? exception);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "AWS telemetry export to {Service} in {Region} was not sent: {Reason}.")]
    private static partial void LogNotSentAgain(ILogger logger, string service, string region, string reason, Exception? exception);

    private static string Describe(CredentialFailure failure) => failure switch
    {
        CredentialFailure.None => "no AWS credential was found (set AwsTelemetry:AccessKeyId and AwsTelemetry:SecretAccessKey, or give the host an IAM role)",
        CredentialFailure.Threw => "resolving the AWS credential failed",
        CredentialFailure.Pending => "the AWS credential is still being resolved",
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown credential failure."),
    };

    private AWSSigningRequest PrepareSigningRequest(HttpRequestMessage request, byte[]? body)
    {
        foreach (var header in _addedHeaders)
        {
            request.Headers.Remove(header.Key);
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var signing = new AWSSigningRequest
        {
            HttpMethod = request.Method,
            RequestUri = request.RequestUri,
            Content = body,
        };

        var headers = request.Content is null
            ? request.Headers.NonValidated
            : request.Headers.NonValidated.Concat(request.Content.Headers.NonValidated);
        foreach (var header in headers.Where(static header => !IsExcludedFromSigning(header.Key)))
        {
            signing.Headers[header.Key] = JoinValues(header.Value);
        }

        return signing;
    }

    private AWSSigV4Parameters Parameters(AWSCredentials credentials) => new()
    {
        Credentials = credentials,
        Region = _region,
        Service = _service,
        SignPayload = true,
        SignedAt = _timeProvider.GetUtcNow().UtcDateTime,
    };

    /// <summary>One Warning per kind of failure for the life of the handler; repeats go to Debug.</summary>
    private void Warn(ref int warned, CredentialFailure failure, Exception? exception)
    {
        var reason = Describe(failure);
        if (Interlocked.Exchange(ref warned, 1) == 0)
        {
            LogNotSent(_logger, _service, _region.SystemName, reason, exception);
        }
        else
        {
            LogNotSentAgain(_logger, _service, _region.SystemName, reason, exception);
        }
    }

    private static string JoinValues(HeaderStringValues values) =>
        string.Join(',', values.Select(static value => value.Trim()));

    private enum CredentialFailure
    {
        None,
        Threw,
        Pending,
    }

    /// <summary>
    /// Awaits one credential lookup and keeps its outcome, so the synchronous path reads the result
    /// of a lookup that already finished without a blocking wait. It never faults: a failure is kept,
    /// not thrown, so a lookup left running in the background cannot surface as an unobserved fault.
    /// </summary>
    private sealed class CredentialCapture
    {
        /// <summary>The resolved credentials, or <see langword="null"/>.</summary>
        public AWSCredentials? Credentials { get; private set; }

        /// <summary>The lookup's failure, or <see langword="null"/>.</summary>
        public Exception? Failure { get; private set; }

        /// <summary>Awaits the lookup and records its outcome.</summary>
        /// <param name="lookup">The lookup.</param>
        /// <returns>A task that completes when the outcome is recorded.</returns>
        public async Task RunAsync(ValueTask<AWSCredentials?> lookup)
        {
            try
            {
                Credentials = await lookup.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Failure = exception;
            }
        }
    }
}
