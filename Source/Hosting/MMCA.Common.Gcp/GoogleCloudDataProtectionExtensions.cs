using Google.Cloud.Kms.V1;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Gcp.DataProtection;

namespace MMCA.Common.Gcp;

/// <summary>
/// Registration extension for a shared ASP.NET Core DataProtection key ring on Google Cloud.
/// <para>
/// The framework default keeps the key ring in memory, which is correct for a single-process host
/// and wrong for a scaled-out one: every replica generates its own keys, so an auth cookie or an
/// antiforgery token minted by replica A cannot be decrypted by replica B. The user sees random
/// sign-outs and "The antiforgery token could not be decrypted" errors that follow no pattern
/// because they follow the load balancer. Persisting the key ring to a single Cloud Storage object
/// gives every replica the same keys, which is what makes cookies and tokens portable across the
/// deployment.
/// </para>
/// </summary>
public static class GoogleCloudDataProtectionExtensions
{
    private const string ObjectUriKey = "DataProtection:GcsObjectUri";
    private const string KmsKeyNameKey = "DataProtection:GcpKmsKeyName";

    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        /// <summary>
        /// Persists the DataProtection key ring to one Google Cloud Storage object so that every
        /// replica of a scaled-out host shares one key ring, and optionally encrypts the keys at rest
        /// with a Cloud KMS key.
        /// <para>
        /// Configuration keys:
        /// <list type="bullet">
        ///   <item><c>DataProtection:GcsObjectUri</c> (the gate): the object that holds the key ring
        ///     XML, as <c>gs://bucket/path/keys.xml</c>. When absent or whitespace this method does
        ///     nothing at all, so local development and tests keep the in-memory default and take no
        ///     Google Cloud dependency at startup. The bucket is provisioned by infrastructure; the
        ///     object is created on the first key.</item>
        ///   <item><c>DataProtection:GcpKmsKeyName</c> (optional): the full name of the Cloud KMS
        ///     symmetric key that encrypts each key at rest,
        ///     <c>projects/{project}/locations/{location}/keyRings/{ring}/cryptoKeys/{key}</c>.</item>
        ///   <item><c>DataProtection:ApplicationName</c> (optional): the application discriminator
        ///     that isolates one application's keys from another's when several share a key ring.
        ///     Defaults to the host application name, as in the Azure and AWS siblings.</item>
        /// </list>
        /// </para>
        /// <para>
        /// The object is one document rewritten under a generation precondition, so two replicas
        /// creating a key at the same moment cannot overwrite each other: the loser re-reads and
        /// retries. The Cloud Storage client is the <c>StorageClient</c> the host registered (an
        /// emulator endpoint is set there, or it is the one <c>AddGoogleCloudStorage</c> registers);
        /// otherwise one is built over Application Default Credentials. The KMS client is likewise a
        /// registered <c>KeyManagementServiceClient</c>, else a default one. The identity needs
        /// <c>storage.objects.get</c>, <c>storage.objects.create</c> and <c>storage.objects.delete</c>
        /// on the bucket (overwriting an object needs the last; <c>roles/storage.objectUser</c> covers
        /// all three), plus <c>roles/cloudkms.cryptoKeyEncrypterDecrypter</c> on the key when one is
        /// configured.
        /// </para>
        /// <para>
        /// Encryption is a separate, optional step for the reason the Azure sibling gives: the
        /// persistence is what fixes cross-replica decryption and must work on its own. A key named
        /// here is used for every key written from then on, so grant the KMS role before setting
        /// it. A host keeps exactly one key ring: setting this object and
        /// <c>DataProtection:BlobStorageUri</c> or <c>DataProtection:SsmParameterPrefix</c> together
        /// fails the startup tripwire.
        /// </para>
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// <c>DataProtection:GcsObjectUri</c> is not of the <c>gs://bucket/object</c> form, or
        /// <c>DataProtection:GcpKmsKeyName</c> is set but is not a full crypto key name.
        /// </exception>
        public TBuilder AddGoogleCloudDataProtection()
        {
            var objectUri = builder.Configuration[ObjectUriKey];

            // The gate. Absent means "do nothing": a developer machine and a test host run
            // single-process, where the in-memory default is correct and an unconditional Google
            // Cloud dependency at startup would be a liability, not a feature. No receipt either, so
            // an object that only appears later still trips the startup check.
            if (string.IsNullOrWhiteSpace(objectUri))
            {
                return builder;
            }

            // Both values fail here, naming their key, before anything is registered.
            var location = GcsObjectLocation.Parse(objectUri, ObjectUriKey);
            var kmsKeyNameSetting = builder.Configuration[KmsKeyNameKey];
            CryptoKeyName? kmsKeyName = null;
            if (!string.IsNullOrWhiteSpace(kmsKeyNameSetting)
                && !CryptoKeyName.TryParse(kmsKeyNameSetting, out kmsKeyName))
            {
                throw new InvalidOperationException(
                    $"{KmsKeyNameKey} must be a full Cloud KMS key name, projects/{{project}}/locations/{{location}}/keyRings/{{ring}}/cryptoKeys/{{key}}, but was \"{kmsKeyNameSetting}\".");
            }

            // The receipt the core startup tripwire checks: DataProtection:GcsObjectUri set without
            // this call fails the host instead of running every replica on its own key ring.
            builder.Services.AddSingleton(new HostIntegrationMarker(HostIntegrationNames.GoogleCloudDataProtection));

            var applicationName = builder.Configuration["DataProtection:ApplicationName"]
                ?? builder.Environment.ApplicationName;

            var dataProtection = builder.Services.AddDataProtection()
                .SetApplicationName(applicationName)
                .PersistKeysToGoogleCloudStorage(location);

            if (kmsKeyName is not null)
            {
                dataProtection.ProtectKeysWithGoogleCloudKms(kmsKeyName);
            }

            return builder;
        }
    }
}
