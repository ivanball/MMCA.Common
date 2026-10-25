using System.Net;
using Google;
using Google.Apis.Download;
using Google.Apis.Upload;
using Google.Cloud.Storage.V1;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace MMCA.Common.Gcp.Tests.DataProtection;

/// <summary>
/// An in-memory Cloud Storage with the generation semantics the key ring relies on: every write
/// mints a new generation, an upload carrying <c>IfGenerationMatch</c> fails with 412 unless it
/// names the current one (0 meaning "must not exist"), a read pinned to a replaced generation is a
/// 404, and a missing object is a 404. Only the three calls the key ring makes are implemented.
/// </summary>
internal sealed class InMemoryGcsStorageClient : StorageClient
{
    private readonly Dictionary<(string Bucket, string Name), (byte[] Content, long Generation)> _objects = [];
    private readonly Lock _gate = new();
    private long _nextGeneration = 1_700_000_000_000_000;

    /// <summary>Gets the options of every upload attempt, accepted or rejected, in order.</summary>
    public List<UploadObjectOptions?> Uploads { get; } = [];

    /// <summary>Gets or sets a one-shot hook run before the next upload is checked, to stage a concurrent writer.</summary>
    public Action? BeforeNextUpload { get; set; }

    /// <summary>Gets or sets a hook run before EVERY upload is checked, to stage a writer that always wins.</summary>
    public Action? BeforeEveryUpload { get; set; }

    /// <summary>Gets the current content of an object as text, or <see langword="null"/> when it does not exist.</summary>
    public string? ContentOf(string bucket, string name)
    {
        lock (_gate)
        {
            return _objects.TryGetValue((bucket, name), out var stored) ? System.Text.Encoding.UTF8.GetString(stored.Content) : null;
        }
    }

    /// <summary>Writes an object unconditionally, as another process would.</summary>
    public void Put(string bucket, string name, string content)
    {
        lock (_gate)
        {
            _objects[(bucket, name)] = (System.Text.Encoding.UTF8.GetBytes(content), ++_nextGeneration);
        }
    }

    public override StorageObject GetObject(string bucket, string objectName, GetObjectOptions? options = null)
    {
        lock (_gate)
        {
            return _objects.TryGetValue((bucket, objectName), out var stored)
                ? new StorageObject { Bucket = bucket, Name = objectName, Generation = stored.Generation }
                : throw NotFound();
        }
    }

    public override StorageObject DownloadObject(
        string bucket,
        string objectName,
        Stream destination,
        DownloadObjectOptions? options = null,
        IProgress<IDownloadProgress>? progress = null)
    {
        byte[] content;
        long generation;
        lock (_gate)
        {
            if (!_objects.TryGetValue((bucket, objectName), out var stored)
                || options?.Generation is { } pinned && pinned != stored.Generation)
            {
                throw NotFound();
            }

            (content, generation) = stored;
        }

        destination.Write(content);
        return new StorageObject { Bucket = bucket, Name = objectName, Generation = generation };
    }

    public override StorageObject UploadObject(
        StorageObject destination,
        Stream source,
        UploadObjectOptions? options = null,
        IProgress<IUploadProgress>? progress = null)
    {
        var hook = BeforeNextUpload;
        BeforeNextUpload = null;
        hook?.Invoke();
        BeforeEveryUpload?.Invoke();

        using var buffer = new MemoryStream();
        source.CopyTo(buffer);

        lock (_gate)
        {
            Uploads.Add(options);
            var key = (destination.Bucket, destination.Name);
            var current = _objects.TryGetValue(key, out var stored) ? stored.Generation : 0;
            if (options?.IfGenerationMatch is { } expected && expected != current)
            {
                throw new GoogleApiException("storage", "conditionNotMet") { HttpStatusCode = HttpStatusCode.PreconditionFailed };
            }

            var generation = ++_nextGeneration;
            _objects[key] = (buffer.ToArray(), generation);
            return new StorageObject { Bucket = destination.Bucket, Name = destination.Name, Generation = generation, ContentType = destination.ContentType };
        }
    }

    private static GoogleApiException NotFound() =>
        new("storage", "No such object") { HttpStatusCode = HttpStatusCode.NotFound };
}
