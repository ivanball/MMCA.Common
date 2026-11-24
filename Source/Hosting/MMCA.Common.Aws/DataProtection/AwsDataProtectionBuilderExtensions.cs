using Amazon.S3;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MMCA.Common.Aws.DataProtection;

/// <summary>
/// The <see cref="IDataProtectionBuilder"/> step behind <c>AddAwsDataProtection</c>, shaped like the
/// framework's own <c>PersistKeysTo...</c> calls: it sets <see cref="KeyManagementOptions.XmlRepository"/>
/// through an options configuration, so the S3 client is resolved from the container when
/// DataProtection first builds its options, not at registration.
/// </summary>
internal static class AwsDataProtectionBuilderExtensions
{
    /// <summary>The backoff unit between conditional-write attempts on the key-ring object.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Persists the key ring to one S3 object. The client is the <see cref="IAmazonS3"/> the host
    /// registered (an emulator endpoint, or the one <c>AddS3FileStorage</c> registers), used as is;
    /// otherwise one is built from the AWS SDK defaults (region and credentials from the default
    /// chain). It is deliberately NOT added to the container: a default client registered here would
    /// win the TryAdd in a later <c>AddS3FileStorage</c> and silently ignore its <c>FileStorage</c>
    /// endpoint settings.
    /// </summary>
    /// <param name="builder">The DataProtection builder.</param>
    /// <param name="location">The object that holds the key ring.</param>
    /// <param name="kmsKeyId">The KMS key every write encrypts under (SSE-KMS), or <see langword="null"/> for the bucket default.</param>
    /// <returns>The same builder for chaining.</returns>
    internal static IDataProtectionBuilder PersistKeysToAmazonS3(this IDataProtectionBuilder builder, S3ObjectLocation location, string? kmsKeyId)
    {
        builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(services =>
            new ConfigureOptions<KeyManagementOptions>(options =>
                options.XmlRepository = new S3XmlRepository(
                    services.GetService<IAmazonS3>() ?? new AmazonS3Client(),
                    location,
                    kmsKeyId,
                    RetryDelay)));

        return builder;
    }
}
