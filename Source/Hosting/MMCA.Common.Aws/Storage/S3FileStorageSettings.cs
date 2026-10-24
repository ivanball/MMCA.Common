using Amazon.Runtime;

namespace MMCA.Common.Aws.Storage;

/// <summary>
/// S3-compatible file-storage settings bound from the <c>FileStorage</c> configuration section
/// (ADR-045), the same section the Azure adapter binds. <see cref="Bucket"/> is the gate: without it
/// the section is incomplete and <c>AddS3FileStorage</c> leaves the unconfigured default in place.
/// One client serves AWS S3 (set <see cref="Region"/>), Cloudflare R2, MinIO, the GCS XML API and
/// LocalStack (set <see cref="ServiceUrl"/>, plus <see cref="ForcePathStyle"/> for MinIO and
/// LocalStack).
/// </summary>
public sealed class S3FileStorageSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "FileStorage";

    /// <summary>The bucket every object lives in. Required; the bucket itself is provisioned by infrastructure, not created here.</summary>
    public string? Bucket { get; init; }

    /// <summary>
    /// The AWS region (system name, e.g. <c>us-east-1</c>). With <see cref="ServiceUrl"/> it is the
    /// signing region instead (Cloudflare R2 expects <c>auto</c>). When blank the AWS SDK resolves
    /// the region from its default chain (<c>AWS_REGION</c>, the shared profile, instance metadata).
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// The endpoint of an S3-compatible service, e.g. <c>https://&lt;account&gt;.r2.cloudflarestorage.com</c>,
    /// <c>https://storage.googleapis.com</c>, a MinIO or LocalStack URL. Leave blank for AWS S3. Only
    /// an absolute URL counts.
    /// </summary>
    public Uri? ServiceUrl { get; init; }

    /// <summary>
    /// Addresses objects as <c>{endpoint}/{bucket}/{key}</c> instead of the virtual-hosted
    /// <c>{bucket}.{endpoint}/{key}</c>. MinIO and LocalStack need it; AWS S3 and R2 do not.
    /// </summary>
    public bool ForcePathStyle { get; init; }

    /// <summary>
    /// Optional base for the URL an upload returns, e.g. a CDN in front of the bucket or an R2 public
    /// bucket domain: the result is <c>{PublicBaseUrl}/{key}</c>. When blank the object URL is derived
    /// from the endpoint, the bucket and the addressing style. Only an absolute URL counts.
    /// </summary>
    public Uri? PublicBaseUrl { get; init; }

    /// <summary>
    /// Optional static access key. Set it together with <see cref="SecretAccessKey"/> (R2, MinIO and
    /// GCS HMAC keys need them); when both are blank the AWS default credential chain applies (an IAM
    /// role, the environment, the shared profile). Setting only one of the two fails registration.
    /// </summary>
    public string? AccessKeyId { get; init; }

    /// <summary>The secret paired with <see cref="AccessKeyId"/>.</summary>
    public string? SecretAccessKey { get; init; }

    /// <summary>
    /// When the client computes request checksums. Defaults to <c>WHEN_REQUIRED</c>, not the AWS SDK
    /// v4 default of <c>WHEN_SUPPORTED</c>: the v4 SDK attaches integrity checksums (CRC32 trailers,
    /// <c>x-amz-checksum-*</c> headers) to every upload it can, and several S3-compatible services
    /// (Cloudflare R2, older MinIO releases, the GCS XML API) reject those requests. Set
    /// <c>WHEN_SUPPORTED</c> only against AWS S3 itself.
    /// </summary>
    public RequestChecksumCalculation RequestChecksumCalculation { get; init; } = RequestChecksumCalculation.WHEN_REQUIRED;

    /// <summary>
    /// When the client validates response checksums. Defaults to <c>WHEN_REQUIRED</c> for the same
    /// S3-compatible services as <see cref="RequestChecksumCalculation"/>, which do not all return the
    /// checksum headers the v4 SDK otherwise expects.
    /// </summary>
    public ResponseChecksumValidation ResponseChecksumValidation { get; init; } = ResponseChecksumValidation.WHEN_REQUIRED;
}
