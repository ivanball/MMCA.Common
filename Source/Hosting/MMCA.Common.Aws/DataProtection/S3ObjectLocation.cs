namespace MMCA.Common.Aws.DataProtection;

/// <summary>
/// The S3 object that holds the key ring, parsed from the <c>s3://bucket/key</c> form of
/// <c>DataProtection:S3ObjectUri</c>. Parsed by hand rather than through <see cref="Uri"/>, which
/// lower-cases and re-escapes an unknown scheme's authority and path in ways a bucket or object key
/// must not be.
/// </summary>
/// <param name="Bucket">The bucket name.</param>
/// <param name="Key">The object key within the bucket.</param>
internal sealed record S3ObjectLocation(string Bucket, string Key)
{
    private const string Scheme = "s3://";

    /// <summary>
    /// Parses an <c>s3://bucket/key</c> value. The bucket and the key must both be non-empty, and the
    /// key must not end in <c>/</c> (that names a folder, not an object).
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <param name="configurationKey">The configuration key it came from, named in the failure.</param>
    /// <returns>The location.</returns>
    /// <exception cref="InvalidOperationException">The value is not of the <c>s3://bucket/key</c> form.</exception>
    internal static S3ObjectLocation Parse(string value, string configurationKey)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            var rest = value[Scheme.Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && slash < rest.Length - 1 && !rest.AsSpan().EndsWith('/'))
            {
                return new S3ObjectLocation(rest[..slash], rest[(slash + 1)..]);
            }
        }

        throw new InvalidOperationException(
            $"{configurationKey} must name one S3 object as s3://bucket/path/keys.xml, but was \"{value}\".");
    }

    /// <inheritdoc />
    public override string ToString() => $"{Scheme}{Bucket}/{Key}";
}
