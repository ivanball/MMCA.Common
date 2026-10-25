using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Google;
using Google.Cloud.Storage.V1;
using Microsoft.AspNetCore.DataProtection.Repositories;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace MMCA.Common.Gcp.DataProtection;

/// <summary>
/// An <see cref="IXmlRepository"/> that keeps the whole DataProtection key ring as ONE Cloud Storage
/// object, the shape <c>Azure.Extensions.AspNetCore.DataProtection.Blobs</c> uses for its blob: a
/// <c>&lt;repository&gt;</c> document whose children are the key elements.
/// <para>
/// Writes are read-modify-write under an object generation precondition, the Cloud Storage
/// counterpart of the Blob repository's ETag: the write carries <c>IfGenerationMatch</c> set to the
/// generation it read (0 when the object did not exist, which lets the write only create it), so two
/// replicas adding a key at the same moment cannot overwrite each other. The loser gets 412, re-reads
/// the ring, which now holds the winner's key, and tries again, up to <see cref="MaxAttempts"/> times
/// with a short linear backoff. Reads pin the generation the metadata call reported, so the
/// generation a write is conditioned on is always the one whose content it extended.
/// </para>
/// <para>
/// <see cref="IXmlRepository"/> is synchronous, and so are the calls here: the Cloud Storage client
/// has synchronous methods, so nothing blocks on a task. DataProtection reads the ring rarely (at
/// startup and on its refresh period) and writes it only when it creates a key.
/// </para>
/// </summary>
internal sealed class GcsXmlRepository : IXmlRepository
{
    /// <summary>How many conditional writes one <see cref="StoreElement"/> attempts before giving up.</summary>
    internal const int MaxAttempts = 10;

    private const string RootElementName = "repository";

    /// <summary>UTF-8 without a byte order mark, so the stored object is plain XML text.</summary>
    private static readonly XmlWriterSettings WriterSettings = new() { Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) };

    private readonly StorageClient _client;
    private readonly TimeSpan _retryDelay;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="client">The Cloud Storage client.</param>
    /// <param name="location">The object that holds the key ring.</param>
    /// <param name="retryDelay">The backoff unit between conditional-write attempts (attempt n waits n units).</param>
    internal GcsXmlRepository(StorageClient client, GcsObjectLocation location, TimeSpan retryDelay)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Location = location ?? throw new ArgumentNullException(nameof(location));
        _retryDelay = retryDelay;
    }

    /// <summary>Gets the object that holds the key ring.</summary>
    internal GcsObjectLocation Location { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements() =>
        Read().Document.Root!.Elements().ToList().AsReadOnly();

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var (document, generation) = Read();

            // A copy, so the caller's element is never re-parented into a document a retry discards.
            document.Root!.Add(new XElement(element));

            using var content = new MemoryStream();
            using (var writer = XmlWriter.Create(content, WriterSettings))
            {
                document.Save(writer);
            }

            content.Position = 0;

            try
            {
                _client.UploadObject(
                    new StorageObject { Bucket = Location.Bucket, Name = Location.ObjectName, ContentType = "application/xml" },
                    content,
                    new UploadObjectOptions { IfGenerationMatch = generation });
                return;
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.PreconditionFailed)
            {
                // Another writer replaced the object after this read. Back off and re-read: the next
                // attempt extends the ring the winner wrote, so neither key is lost.
                if (attempt < MaxAttempts && _retryDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(_retryDelay * attempt);
                }
            }
        }

        throw new InvalidOperationException(
            $"The DataProtection key ring at {Location} could not be written: another writer replaced it on each of {MaxAttempts} attempts.");
    }

    private (XDocument Document, long Generation) Read()
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            StorageObject metadata;
            try
            {
                metadata = _client.GetObject(Location.Bucket, Location.ObjectName);
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                // Nobody has written the ring yet: an empty one, at generation 0 ("must not exist").
                return (new XDocument(new XElement(RootElementName)), 0);
            }

            using var content = new MemoryStream();
            try
            {
                _client.DownloadObject(
                    Location.Bucket,
                    Location.ObjectName,
                    content,
                    new DownloadObjectOptions { Generation = metadata.Generation });
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound && attempt < MaxAttempts)
            {
                // The generation was replaced between the two calls; read the new one.
                continue;
            }

            content.Position = 0;
            return (XDocument.Load(content), metadata.Generation ?? 0);
        }
    }
}
