using Google.Cloud.Storage.V1;

namespace MMCA.Common.Gcp.Storage;

/// <summary>
/// Builds the Cloud Storage client <c>AddGoogleCloudStorage</c> registers, from
/// <see cref="GcsFileStorageSettings"/>. Split from the registration so the client configuration
/// (endpoint, credential posture, quota project) is asserted directly rather than through a live
/// request.
/// </summary>
internal static class GcsStorageClientFactory
{
    /// <summary>
    /// Maps the settings onto a <see cref="StorageClientBuilder"/>. Without a
    /// <see cref="GcsFileStorageSettings.GcsServiceUrl"/> the builder keeps the Google Cloud endpoint
    /// and Application Default Credentials. With one, the JSON API root becomes
    /// <c>{GcsServiceUrl}/storage/v1/</c>, and a plain-HTTP endpoint (an emulator) is called without
    /// credentials. The <c>STORAGE_EMULATOR_HOST</c> variable is ignored (the builder default), so the
    /// endpoint is decided by configuration alone.
    /// </summary>
    /// <param name="settings">The bound settings.</param>
    /// <returns>The client builder.</returns>
    internal static StorageClientBuilder BuildBuilder(GcsFileStorageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var builder = new StorageClientBuilder();

        if (settings.GcsServiceUrl is { IsAbsoluteUri: true } serviceUrl)
        {
            builder.BaseUri = $"{serviceUrl.AbsoluteUri.TrimEnd('/')}/storage/v1/";
            builder.UnauthenticatedAccess = string.Equals(serviceUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(settings.GcsProjectId))
        {
            builder.QuotaProject = settings.GcsProjectId;
        }

        return builder;
    }

    /// <summary>Creates the client from <see cref="BuildBuilder"/>.</summary>
    /// <param name="settings">The bound settings.</param>
    /// <returns>The Cloud Storage client.</returns>
    internal static StorageClient Create(GcsFileStorageSettings settings) => BuildBuilder(settings).Build();
}
