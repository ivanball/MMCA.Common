namespace MMCA.Common.Gcp.Storage;

/// <summary>
/// Google Cloud Storage file-storage settings bound from the <c>FileStorage</c> configuration section
/// (ADR-045), the section the Azure (<c>ContainerName</c>) and S3 (<c>Bucket</c>) adapters bind too.
/// Every key here carries the <c>Gcs</c> prefix so one shared section can never make two adapters
/// claim it: <see cref="GcsBucket"/> is the gate, and without it <c>AddGoogleCloudStorage</c> leaves
/// the unconfigured default in place. The client authenticates with Application Default Credentials
/// (workload identity on GKE and Cloud Run, the attached service account on Compute Engine,
/// <c>gcloud auth application-default login</c> on a workstation); there are no HMAC keys.
/// </summary>
public sealed class GcsFileStorageSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "FileStorage";

    /// <summary>The bucket every object lives in. Required; the bucket itself is provisioned by infrastructure, not created here.</summary>
    public string? GcsBucket { get; init; }

    /// <summary>
    /// Optional base for the URL an upload returns, e.g. a CDN or a load balancer domain in front of
    /// the bucket: the result is <c>{GcsPublicBaseUrl}/{object}</c>. When blank the URL is
    /// <c>https://storage.googleapis.com/{bucket}/{object}</c> (or the <see cref="GcsServiceUrl"/>
    /// equivalent). Only an absolute URL counts.
    /// </summary>
    public Uri? GcsPublicBaseUrl { get; init; }

    /// <summary>
    /// Optional endpoint override, the root of a Cloud Storage compatible JSON API such as the
    /// fake-gcs-server emulator (<c>http://localhost:4443</c>); the client then calls
    /// <c>{GcsServiceUrl}/storage/v1/</c>. A plain-HTTP endpoint is treated as an emulator and called
    /// without credentials, so no bearer token ever travels in clear; an HTTPS endpoint (a Private
    /// Service Connect name, for instance) keeps Application Default Credentials. Leave blank for
    /// Google Cloud itself. Only an absolute URL counts.
    /// </summary>
    public Uri? GcsServiceUrl { get; init; }

    /// <summary>
    /// Optional project billed for the requests' quota (the <c>x-goog-user-project</c> header). Only
    /// needed when the credentials are a user account rather than a service account, for example a
    /// developer's <c>gcloud auth application-default login</c>; object operations otherwise address
    /// the bucket alone.
    /// </summary>
    public string? GcsProjectId { get; init; }
}
