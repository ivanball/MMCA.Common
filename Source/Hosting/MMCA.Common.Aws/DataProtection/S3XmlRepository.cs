using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace MMCA.Common.Aws.DataProtection;

/// <summary>
/// An <see cref="IXmlRepository"/> that keeps the whole DataProtection key ring as ONE S3 object, the
/// shape the Cloud Storage and Blob repositories use: a <c>&lt;repository&gt;</c> document whose
/// children are the key elements.
/// <para>
/// Writes are read-modify-write under an S3 conditional write, the counterpart of the Blob
/// repository's ETag and the Cloud Storage generation precondition: the write carries
/// <c>If-Match</c> set to the ETag it read (or <c>If-None-Match: *</c> when the object did not exist,
/// which lets the write only create it), so two replicas adding a key at the same moment cannot
/// overwrite each other. The loser gets 412 (or 409 when S3 sees the two conditional writes overlap),
/// re-reads the ring, which now holds the winner's key, and tries again, up to
/// <see cref="MaxAttempts"/> times with a short linear backoff. A read returns the content and its
/// ETag in one response, so the ETag a write is conditioned on is always the one whose content it
/// extended.
/// </para>
/// <para>
/// When a KMS key is configured every write asks for SSE-KMS under it; otherwise the bucket's default
/// encryption applies (S3 encrypts every new object at rest).
/// </para>
/// <para>
/// <see cref="IXmlRepository"/> is synchronous and the AWS SDK is async only, so each call waits on
/// the SDK task off the caller's synchronization context. DataProtection reads the ring rarely (at
/// startup and on its refresh period) and writes it only when it creates a key.
/// </para>
/// </summary>
internal sealed class S3XmlRepository : IXmlRepository
{
    /// <summary>How many conditional writes one <see cref="StoreElement"/> attempts before giving up.</summary>
    internal const int MaxAttempts = 10;

    private const string RootElementName = "repository";

    /// <summary>UTF-8 without a byte order mark, so the stored object is plain XML text.</summary>
    private static readonly XmlWriterSettings WriterSettings = new() { Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) };

    private readonly IAmazonS3 _client;
    private readonly string? _kmsKeyId;
    private readonly TimeSpan _retryDelay;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="client">The S3 client.</param>
    /// <param name="location">The object that holds the key ring.</param>
    /// <param name="kmsKeyId">The KMS key every write encrypts under (SSE-KMS), or <see langword="null"/> for the bucket default.</param>
    /// <param name="retryDelay">The backoff unit between conditional-write attempts (attempt n waits n units).</param>
    internal S3XmlRepository(IAmazonS3 client, S3ObjectLocation location, string? kmsKeyId, TimeSpan retryDelay)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Location = location ?? throw new ArgumentNullException(nameof(location));
        _kmsKeyId = string.IsNullOrWhiteSpace(kmsKeyId) ? null : kmsKeyId;
        _retryDelay = retryDelay;
    }

    /// <summary>Gets the object that holds the key ring.</summary>
    internal S3ObjectLocation Location { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements() =>
        Wait(ReadAsync).Document.Root!.Elements().ToList().AsReadOnly();

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var (document, eTag) = Wait(ReadAsync);

            // A copy, so the caller's element is never re-parented into a document a retry discards.
            document.Root!.Add(new XElement(element));

            using var content = new MemoryStream();
            using (var writer = XmlWriter.Create(content, WriterSettings))
            {
                document.Save(writer);
            }

            content.Position = 0;

            var request = new PutObjectRequest
            {
                BucketName = Location.Bucket,
                Key = Location.Key,
                InputStream = content,
                AutoCloseStream = false,
                ContentType = "application/xml",
            };

            if (eTag is null)
            {
                request.IfNoneMatch = "*";
            }
            else
            {
                request.IfMatch = eTag;
            }

            if (_kmsKeyId is not null)
            {
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS;
                request.ServerSideEncryptionKeyManagementServiceKeyId = _kmsKeyId;
            }

            try
            {
                Wait(() => _client.PutObjectAsync(request));
                return;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            {
                // Another writer replaced (or was replacing) the object after this read. Back off and
                // re-read: the next attempt extends the ring the winner wrote, so neither key is lost.
                if (attempt < MaxAttempts && _retryDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(_retryDelay * attempt);
                }
            }
        }

        throw new InvalidOperationException(
            $"The DataProtection key ring at {Location} could not be written: another writer replaced it on each of {MaxAttempts} attempts.");
    }

    /// <summary>
    /// Waits on an SDK call from the synchronous <see cref="IXmlRepository"/> contract. The call runs
    /// on the thread pool, so a caller holding a synchronization context (a Blazor circuit) cannot
    /// deadlock it.
    /// </summary>
#pragma warning disable VSTHRD002 // Synchronous wait: IXmlRepository is sync by contract and the SDK is async only.
    private static T Wait<T>(Func<Task<T>> operation) => Task.Run(operation).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

    private async Task<(XDocument Document, string? ETag)> ReadAsync()
    {
        try
        {
            using var response = await _client.GetObjectAsync(
                new GetObjectRequest { BucketName = Location.Bucket, Key = Location.Key }).ConfigureAwait(false);

            var document = await XDocument.LoadAsync(response.ResponseStream, LoadOptions.None, CancellationToken.None).ConfigureAwait(false);
            return (document, response.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Nobody has written the ring yet: an empty one, written with "must not exist".
            return (new XDocument(new XElement(RootElementName)), null);
        }
    }
}
