using System.Globalization;
using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Moq;

namespace MMCA.Common.Aws.Tests.DataProtection;

/// <summary>
/// An in-memory S3 with the conditional-write semantics the key ring relies on: every write mints a
/// new ETag, a put carrying <c>If-Match</c> fails with 412 unless it names the current ETag, a put
/// carrying <c>If-None-Match: *</c> fails with 412 when the object exists, and a missing object is a
/// 404. Only the two calls the key ring makes are implemented, over a strict mock.
/// </summary>
internal sealed class InMemoryS3
{
    private readonly Dictionary<(string Bucket, string Key), (byte[] Content, string ETag)> _objects = [];
    private readonly Lock _gate = new();
    private int _nextETag;

    public InMemoryS3()
    {
        Mock = new Mock<IAmazonS3>(MockBehavior.Strict);
        Mock.Setup(s3 => s3.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .Returns((GetObjectRequest request, CancellationToken _) => Task.FromResult(Get(request)));
        Mock.Setup(s3 => s3.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Returns((PutObjectRequest request, CancellationToken _) => Task.FromResult(Put(request)));
    }

    /// <summary>Gets the mock whose object is handed to the repository.</summary>
    public Mock<IAmazonS3> Mock { get; }

    /// <summary>Gets every put attempt, accepted or rejected, in order.</summary>
    public List<PutObjectRequest> Puts { get; } = [];

    /// <summary>Gets or sets a one-shot hook run before the next put is checked, to stage a concurrent writer.</summary>
    public Action? BeforeNextPut { get; set; }

    /// <summary>Gets or sets a hook run before EVERY put is checked, to stage a writer that always wins.</summary>
    public Action? BeforeEveryPut { get; set; }

    /// <summary>Gets the current content of an object as text, or <see langword="null"/> when it does not exist.</summary>
    public string? ContentOf(string bucket, string key)
    {
        lock (_gate)
        {
            return _objects.TryGetValue((bucket, key), out var stored) ? Encoding.UTF8.GetString(stored.Content) : null;
        }
    }

    /// <summary>Gets the current ETag of an object, or <see langword="null"/> when it does not exist.</summary>
    public string? ETagOf(string bucket, string key)
    {
        lock (_gate)
        {
            return _objects.TryGetValue((bucket, key), out var stored) ? stored.ETag : null;
        }
    }

    /// <summary>Writes an object unconditionally, as another process would.</summary>
    public void Write(string bucket, string key, string content)
    {
        lock (_gate)
        {
            _objects[(bucket, key)] = (Encoding.UTF8.GetBytes(content), NewETag());
        }
    }

    private static AmazonS3Exception Failure(HttpStatusCode status, string code) =>
        new(code, ErrorType.Sender, code, "request-id", status);

    private GetObjectResponse Get(GetObjectRequest request)
    {
        lock (_gate)
        {
            if (!_objects.TryGetValue((request.BucketName, request.Key), out var stored))
            {
                throw Failure(HttpStatusCode.NotFound, "NoSuchKey");
            }

            return new GetObjectResponse { ResponseStream = new MemoryStream(stored.Content), ETag = stored.ETag };
        }
    }

    private PutObjectResponse Put(PutObjectRequest request)
    {
        // The body is read before a staged writer runs, as the request was already on the wire.
        using var content = new MemoryStream();
        request.InputStream.CopyTo(content);

        var hook = BeforeNextPut;
        BeforeNextPut = null;
        hook?.Invoke();
        BeforeEveryPut?.Invoke();

        lock (_gate)
        {
            Puts.Add(request);
            var key = (request.BucketName, request.Key);
            var exists = _objects.TryGetValue(key, out var stored);

            var createOnlyRefused = request.IfNoneMatch == "*" && exists;
            var replaceRefused = request.IfMatch is not null && (!exists || !string.Equals(request.IfMatch, stored.ETag, StringComparison.Ordinal));
            if (createOnlyRefused || replaceRefused)
            {
                throw Failure(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
            }

            var eTag = NewETag();
            _objects[key] = (content.ToArray(), eTag);
            return new PutObjectResponse { ETag = eTag };
        }
    }

    private string NewETag() =>
        string.Create(CultureInfo.InvariantCulture, $"\"etag-{Interlocked.Increment(ref _nextETag)}\"");
}
