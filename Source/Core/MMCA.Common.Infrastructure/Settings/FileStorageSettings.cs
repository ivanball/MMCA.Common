namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Blob file-storage settings bound from the <c>FileStorage</c> configuration section
/// (ADR-045). Production sets <see cref="ServiceUri"/> (managed-identity auth via
/// DefaultAzureCredential); local development can use <see cref="ConnectionString"/>
/// (e.g. Azurite) instead. When neither is set the section is incomplete and
/// <c>AddAzureBlobFileStorage</c> leaves the unconfigured default in place.
/// </summary>
public sealed class FileStorageSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "FileStorage";

    /// <summary>Blob service endpoint, e.g. <c>https://myaccount.blob.core.windows.net</c>. Uses DefaultAzureCredential.</summary>
    public Uri? ServiceUri { get; init; }

    /// <summary>Storage connection string alternative for local development (Azurite).</summary>
    public string? ConnectionString { get; init; }

    /// <summary>The container all blobs live in. Required.</summary>
    public string? ContainerName { get; init; }
}
