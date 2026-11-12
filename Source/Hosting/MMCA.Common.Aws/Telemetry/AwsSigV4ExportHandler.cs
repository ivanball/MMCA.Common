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
/// The signature covers a fixed, minimal header set: <c>host</c>, <c>content-type</c>, the
/// <c>x-amz-*</c> headers the signer adds, and on a logs request <c>x-aws-log-group</c> and
/// <c>x-aws-log-stream</c>. Every other header still goes out but is not signed, so a header whose
/// values the transport joins differently from the signer cannot invalidate the signature.
/// </para>
/// <para>
/// The synchronous path resolves the credential synchronously when it is not resolved yet, so it may
/// block the exporter's own background thread while the default chain answers (as the SDK signer
/// itself does when it refreshes an expiring credential). When no credential can be resolved, or
/// signing throws, the handler sends nothing and answers 401 itself, so no unsigned request ever
/// leaves the process; it logs one Warning per kind of failure, with the exception type and message,
/// and the next export tries again.
/// </para>
/// </summary>
internal sealed partial class AwsSigV4ExportHandler : DelegatingHandler
{
    /// <summary>The one content header that is signed.</summary>
    private const string ContentTypeHeader = "content-type";

    /// <summary>The request headers signed when present: the CloudWatch Logs destination.</summary>
    private static readonly string[] SignedRequestHeaders =
    [
        AwsTelemetryExporterExtensions.LogGroupHeader,
        AwsTelemetryExporterExtensions.LogStreamHeader,
    ];

    private readonly IAwsTelemetryCredentialSource _credentials;

    private readonly RegionEndpoint _region;

    private readonly string _service;

    private readonly TimeProvider _timeProvider;

    private readonly ILogger _logger;

    private readonly KeyValuePair<string, string>[] _addedHeaders;

    private int _warnedNoCredentials;

    private int _warnedCredentialFailure;

    private int _warnedSigningFailure;

    /// <summary>Initializes a new instance of the <see cref="AwsSigV4ExportHandler"/> class.</summary>
    /// <param name="credentials">The credentials every request is signed with.</param>
    /// <param name="region">The signing region.</param>
    /// <param name="service">The signing service name, <c>xray</c> or <c>logs</c>.</param>
    /// <param name="timeProvider">The clock the signing time is read from.</param>
    /// <param name="logger">Logs one Warning per kind of credential or signing failure.</param>
    /// <param name="addedHeaders">
    /// Headers added to every request BEFORE signing (the logs exporter's <c>x-aws-log-group</c> and
    /// <c>x-aws-log-stream</c>, which are part of the signed set). Each replaces any value already present.
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

        // The exporter calls this on its own background thread, which already blocks on the send:
        // an unresolved credential is resolved here, synchronously, rather than the export refused.
        AWSCredentials? credentials;
        try
        {
            credentials = _credentials.Get(cancellationToken);
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

        var body = ReadBody(request, cancellationToken);
        AWSSigningResult result;
        try
        {
            // The signer resolves a refreshing credential itself (AWSCredentials.GetCredentials), so a
            // failed refresh surfaces here.
            result = AWSSigV4Signer.Sign(PrepareSigningRequest(request, body), Parameters(credentials));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Warn(ref _warnedSigningFailure, CredentialFailure.SigningFailed, exception);
            return Refuse(request);
        }

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
        AWSSigningResult result;
        try
        {
            result = await AWSSigV4Signer
                .SignAsync(PrepareSigningRequest(request, body), Parameters(credentials), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Warn(ref _warnedSigningFailure, CredentialFailure.SigningFailed, exception);
            return Refuse(request);
        }

        ApplySignature(request, result);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

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

    /// <summary>The reason a log line gives, with the exception's type and message when there is one (never key material).</summary>
    private static string Describe(CredentialFailure failure, Exception? exception)
    {
        var reason = failure switch
        {
            CredentialFailure.None => "no AWS credential was found (set AwsTelemetry:AccessKeyId and AwsTelemetry:SecretAccessKey, or give the host an IAM role)",
            CredentialFailure.Threw => "resolving the AWS credential failed",
            CredentialFailure.SigningFailed => "signing the request failed",
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown credential failure."),
        };

        return exception is null ? reason : $"{reason} ({exception.GetType().FullName}: {exception.Message})";
    }

    /// <summary>
    /// Adds the configured headers, then builds the signing request over the fixed header set only:
    /// <c>content-type</c> and, when present, the CloudWatch Logs destination headers. The signer adds
    /// <c>host</c> from the URL and its own <c>x-amz-*</c> headers.
    /// </summary>
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

        if (request.Content is not null
            && request.Content.Headers.NonValidated.TryGetValues(ContentTypeHeader, out var contentType))
        {
            signing.Headers[ContentTypeHeader] = JoinValues(contentType);
        }

        foreach (var name in SignedRequestHeaders)
        {
            if (request.Headers.NonValidated.TryGetValues(name, out var values))
            {
                signing.Headers[name] = JoinValues(values);
            }
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
        var reason = Describe(failure, exception);
        if (Interlocked.Exchange(ref warned, 1) == 0)
        {
            LogNotSent(_logger, _service, _region.SystemName, reason, exception);
        }
        else
        {
            LogNotSentAgain(_logger, _service, _region.SystemName, reason, exception);
        }
    }

    /// <summary>One value per header, as the signer expects; the signed headers each carry a single value.</summary>
    private static string JoinValues(HeaderStringValues values) =>
        string.Join(',', values.Select(static value => value.Trim()));

    private enum CredentialFailure
    {
        None,
        Threw,
        SigningFailed,
    }
}
