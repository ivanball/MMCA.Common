namespace MMCA.Common.Gcp.DataProtection;

/// <summary>
/// The Cloud Storage object that holds the key ring, parsed from the <c>gs://bucket/object</c> form
/// of <c>DataProtection:GcsObjectUri</c>. Parsed by hand rather than through <see cref="Uri"/>, which
/// lower-cases and re-escapes an unknown scheme's authority and path in ways a bucket or object name
/// must not be.
/// </summary>
/// <param name="Bucket">The bucket name.</param>
/// <param name="ObjectName">The object name within the bucket.</param>
internal sealed record GcsObjectLocation(string Bucket, string ObjectName)
{
    private const string Scheme = "gs://";

    /// <summary>
    /// Parses a <c>gs://bucket/object</c> value. The bucket and the object name must both be
    /// non-empty, and the object name must not end in <c>/</c> (that names a folder, not an object).
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <param name="key">The configuration key it came from, named in the failure.</param>
    /// <returns>The location.</returns>
    /// <exception cref="InvalidOperationException">The value is not of the <c>gs://bucket/object</c> form.</exception>
    internal static GcsObjectLocation Parse(string value, string key)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            var rest = value[Scheme.Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && slash < rest.Length - 1 && !rest.EndsWith('/'))
            {
                return new GcsObjectLocation(rest[..slash], rest[(slash + 1)..]);
            }
        }

        throw new InvalidOperationException(
            $"{key} must name one Cloud Storage object as gs://bucket/path/keys.xml, but was \"{value}\".");
    }

    /// <inheritdoc />
    public override string ToString() => $"{Scheme}{Bucket}/{ObjectName}";
}
