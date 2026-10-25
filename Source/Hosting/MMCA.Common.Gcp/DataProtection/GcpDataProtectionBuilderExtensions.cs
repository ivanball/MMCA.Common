using Google.Cloud.Kms.V1;
using Google.Cloud.Storage.V1;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMCA.Common.Gcp.Storage;

namespace MMCA.Common.Gcp.DataProtection;

/// <summary>
/// The <see cref="IDataProtectionBuilder"/> steps behind <c>AddGoogleCloudDataProtection</c>, shaped
/// like the framework's own <c>PersistKeysTo...</c> / <c>ProtectKeysWith...</c> pairs: each sets one
/// <see cref="KeyManagementOptions"/> member through an options configuration, so the clients are
/// resolved from the container when DataProtection first builds its options, not at registration.
/// </summary>
internal static class GcpDataProtectionBuilderExtensions
{
    /// <summary>The backoff unit between conditional-write attempts on the key-ring object.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Persists the key ring to one Cloud Storage object. The client is the <see cref="StorageClient"/>
    /// the host registered (an emulator endpoint, or the one <c>AddGoogleCloudStorage</c> registers),
    /// used as is; otherwise one is built with the Google Cloud endpoint and Application Default
    /// Credentials. It is deliberately NOT added to the container: a default client registered here
    /// would win the TryAdd in a later <c>AddGoogleCloudStorage</c> and silently ignore its
    /// <c>FileStorage:GcsServiceUrl</c>.
    /// </summary>
    /// <param name="builder">The DataProtection builder.</param>
    /// <param name="location">The object that holds the key ring.</param>
    /// <returns>The same builder for chaining.</returns>
    internal static IDataProtectionBuilder PersistKeysToGoogleCloudStorage(this IDataProtectionBuilder builder, GcsObjectLocation location)
    {
        builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(services =>
            new ConfigureOptions<KeyManagementOptions>(options =>
                options.XmlRepository = new GcsXmlRepository(
                    services.GetService<StorageClient>() ?? GcsStorageClientFactory.Create(new GcsFileStorageSettings()),
                    location,
                    RetryDelay)));

        return builder;
    }

    /// <summary>
    /// Encrypts each key with a Cloud KMS symmetric key before it is stored. The
    /// <see cref="KeyManagementServiceClient"/> is added to the container only when the host has not
    /// registered one (the decryptor, which DataProtection activates per key, resolves it there).
    /// </summary>
    /// <param name="builder">The DataProtection builder.</param>
    /// <param name="keyName">The symmetric key that encrypts the key ring.</param>
    /// <returns>The same builder for chaining.</returns>
    internal static IDataProtectionBuilder ProtectKeysWithGoogleCloudKms(this IDataProtectionBuilder builder, CryptoKeyName keyName)
    {
        builder.Services.TryAddSingleton(_ => KeyManagementServiceClient.Create());
        builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(services =>
            new ConfigureOptions<KeyManagementOptions>(options =>
                options.XmlEncryptor = new GcpKmsXmlEncryptor(services.GetRequiredService<KeyManagementServiceClient>(), keyName)));

        return builder;
    }
}
