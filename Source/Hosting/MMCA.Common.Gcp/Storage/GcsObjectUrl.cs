namespace MMCA.Common.Gcp.Storage;

/// <summary>
/// Composes the URL an upload returns, the Cloud Storage counterpart of the <c>BlobClient.Uri</c> the
/// Azure adapter returns and of the S3 object URL. Pure string composition: no request is made and no
/// URL is signed, so the result is the object's public address and is only fetchable when the bucket
/// (or the CDN in front of it) serves it.
/// </summary>
internal static class GcsObjectUrl
{
    /// <summary>The public Cloud Storage endpoint objects are addressed on.</summary>
    internal static readonly Uri PublicEndpoint = new("https://storage.googleapis.com/");

    /// <summary>
    /// Composes the object URL, in this order:
    /// <list type="number">
    ///   <item><see cref="GcsFileStorageSettings.GcsPublicBaseUrl"/> set: <c>{GcsPublicBaseUrl}/{object}</c>
    ///   (a CDN or load balancer domain already points at the bucket, so the bucket is not repeated).</item>
    ///   <item><see cref="GcsFileStorageSettings.GcsServiceUrl"/> set: <c>{GcsServiceUrl}/{bucket}/{object}</c>.</item>
    ///   <item>Otherwise <c>https://storage.googleapis.com/{bucket}/{object}</c>.</item>
    /// </list>
    /// Every segment of the object name is percent-encoded and its <c>/</c> separators kept, so a name
    /// with spaces or non-ASCII characters still yields a valid absolute URL.
    /// </summary>
    /// <param name="settings">The bound settings.</param>
    /// <param name="objectName">The object name.</param>
    /// <returns>The absolute object URL.</returns>
    internal static Uri Compose(GcsFileStorageSettings settings, string objectName)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(objectName);

        var escapedName = EscapeName(objectName);

        if (settings.GcsPublicBaseUrl is { IsAbsoluteUri: true } publicBase)
        {
            return new Uri($"{TrimEnd(publicBase)}/{escapedName}");
        }

        var endpoint = settings.GcsServiceUrl is { IsAbsoluteUri: true } serviceUrl ? serviceUrl : PublicEndpoint;
        return new Uri($"{TrimEnd(endpoint)}/{settings.GcsBucket}/{escapedName}");
    }

    private static string EscapeName(string objectName) =>
        string.Join('/', objectName.Split('/').Select(Uri.EscapeDataString));

    private static string TrimEnd(Uri uri) => uri.AbsoluteUri.TrimEnd('/');
}
