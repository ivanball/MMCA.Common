using Amazon.S3;

namespace MMCA.Common.Aws.Storage;

/// <summary>
/// Builds the S3 client <c>AddS3FileStorage</c> registers, from <see cref="S3FileStorageSettings"/>.
/// Split from the registration so the client configuration (endpoint, addressing style, signing
/// region, checksum posture) is asserted directly rather than through a live request.
/// </summary>
internal static class S3StorageClientFactory
{
    /// <summary>The section path named in configuration errors.</summary>
    internal const string SectionPath = "FileStorage";

    /// <summary>
    /// Maps the settings onto an <see cref="AmazonS3Config"/>. A <see cref="S3FileStorageSettings.ServiceUrl"/>
    /// selects an S3-compatible endpoint, and <see cref="S3FileStorageSettings.Region"/> then becomes
    /// its signing region; without one, the region selects the AWS endpoint (see
    /// <see cref="AwsClientEndpoint.Apply"/>).
    /// </summary>
    /// <param name="settings">The bound settings.</param>
    /// <returns>The client configuration.</returns>
    internal static AmazonS3Config BuildConfig(S3FileStorageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var config = new AmazonS3Config
        {
            ForcePathStyle = settings.ForcePathStyle,
            RequestChecksumCalculation = settings.RequestChecksumCalculation,
            ResponseChecksumValidation = settings.ResponseChecksumValidation,
        };

        AwsClientEndpoint.Apply(config, settings.ServiceUrl, settings.Region);

        return config;
    }

    /// <summary>
    /// Creates the client: static credentials when the section carries a complete key pair, else the
    /// AWS default credential chain.
    /// </summary>
    /// <param name="settings">The bound settings.</param>
    /// <returns>The S3 client.</returns>
    internal static IAmazonS3 Create(S3FileStorageSettings settings)
    {
        var config = BuildConfig(settings);
        var credentials = AwsStaticCredentials.Resolve(settings.AccessKeyId, settings.SecretAccessKey, SectionPath);

        return credentials is null
            ? new AmazonS3Client(config)
            : new AmazonS3Client(credentials, config);
    }
}
