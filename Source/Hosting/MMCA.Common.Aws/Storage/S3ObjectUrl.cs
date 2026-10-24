namespace MMCA.Common.Aws.Storage;

/// <summary>
/// Composes the URL an upload returns, the S3 counterpart of the <c>BlobClient.Uri</c> the Azure
/// adapter returns. Pure string composition: no request is made and no URL is signed, so the result
/// is the object's public address and is only fetchable when the bucket (or the CDN in front of it)
/// serves it.
/// </summary>
internal static class S3ObjectUrl
{
    /// <summary>
    /// Composes the object URL, in this order:
    /// <list type="number">
    ///   <item><see cref="S3FileStorageSettings.PublicBaseUrl"/> set: <c>{PublicBaseUrl}/{key}</c> (a
    ///   CDN or an R2 public domain already points at the bucket, so the bucket is not repeated).</item>
    ///   <item><see cref="S3FileStorageSettings.ServiceUrl"/> set: <c>{ServiceUrl}/{bucket}/{key}</c>
    ///   path-style, or <c>{scheme}://{bucket}.{host}/{key}</c> virtual-hosted.</item>
    ///   <item>Otherwise AWS S3 in <paramref name="region"/>: <c>https://s3.{region}.amazonaws.com/{bucket}/{key}</c>
    ///   path-style, or <c>https://{bucket}.s3.{region}.amazonaws.com/{key}</c> virtual-hosted.</item>
    /// </list>
    /// Every segment of the key is percent-encoded and its <c>/</c> separators kept, so a key with
    /// spaces or non-ASCII characters still yields a valid absolute URL.
    /// </summary>
    /// <param name="settings">The bound settings.</param>
    /// <param name="region">The region the client resolved, used only on the AWS S3 branch.</param>
    /// <param name="key">The object key.</param>
    /// <returns>The absolute object URL.</returns>
    /// <exception cref="InvalidOperationException">The AWS S3 branch applies and no region is known.</exception>
    internal static Uri Compose(S3FileStorageSettings settings, string? region, string key)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(key);

        var escapedKey = EscapeKey(key);

        if (settings.PublicBaseUrl is { IsAbsoluteUri: true } publicBase)
        {
            return new Uri($"{TrimEnd(publicBase)}/{escapedKey}");
        }

        var bucket = settings.Bucket;

        if (settings.ServiceUrl is { IsAbsoluteUri: true } serviceUrl)
        {
            if (settings.ForcePathStyle)
            {
                return new Uri($"{TrimEnd(serviceUrl)}/{bucket}/{escapedKey}");
            }

            var builder = new UriBuilder(serviceUrl) { Host = $"{bucket}.{serviceUrl.Host}" };
            return new Uri($"{TrimEnd(builder.Uri)}/{escapedKey}");
        }

        if (string.IsNullOrWhiteSpace(region))
        {
            throw new InvalidOperationException(
                "The S3 object URL cannot be derived: no FileStorage:Region is configured and the AWS SDK resolved none. Set FileStorage:Region, FileStorage:ServiceUrl or FileStorage:PublicBaseUrl.");
        }

        return settings.ForcePathStyle
            ? new Uri($"https://s3.{region}.amazonaws.com/{bucket}/{escapedKey}")
            : new Uri($"https://{bucket}.s3.{region}.amazonaws.com/{escapedKey}");
    }

    private static string EscapeKey(string key) =>
        string.Join('/', key.Split('/').Select(Uri.EscapeDataString));

    private static string TrimEnd(Uri uri) => uri.AbsoluteUri.TrimEnd('/');
}
