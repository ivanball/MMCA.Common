using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MMCA.Common.Aspire;

/// <summary>
/// Registration extension for a shared ASP.NET Core DataProtection key ring.
/// <para>
/// The framework default keeps the key ring in memory, which is correct for a single-process host
/// and wrong for a scaled-out one: every replica generates its own keys, so an auth cookie or an
/// antiforgery token minted by replica A cannot be decrypted by replica B. The user sees random
/// sign-outs and "The antiforgery token could not be decrypted" errors that follow no pattern
/// because they follow the load balancer. Persisting the key ring to a single blob gives every
/// replica the same keys, which is what makes cookies and tokens portable across the deployment.
/// </para>
/// </summary>
public static class DataProtectionExtensions
{
    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        /// <summary>
        /// Persists the DataProtection key ring to Azure Blob Storage so that every replica of a
        /// scaled-out host shares one key ring, and optionally encrypts that key ring at rest with
        /// an Azure Key Vault key.
        /// <para>
        /// Configuration keys:
        /// <list type="bullet">
        ///   <item><c>DataProtection:BlobStorageUri</c> (the gate): full URI of the blob that holds
        ///     the key ring XML, e.g.
        ///     <c>https://mystorage.blob.core.windows.net/dataprotection/keys.xml</c>. When absent
        ///     or whitespace this method does nothing at all, so local development and tests keep
        ///     the in-memory default and take no Azure dependency at startup.</item>
        ///   <item><c>DataProtection:ApplicationName</c> (optional): the application discriminator
        ///     that isolates one application's keys from another's when several share a blob or a
        ///     key ring directory. Defaults to the host application name.</item>
        ///   <item><c>DataProtection:KeyVaultKeyUri</c> (optional): full URI of the Key Vault key
        ///     used to encrypt the key ring at rest, e.g.
        ///     <c>https://myvault.vault.azure.net/keys/dataprotection</c>.</item>
        /// </list>
        /// </para>
        /// <para>
        /// Authentication uses <see cref="DefaultAzureCredential"/>, so a deployed host authenticates
        /// with its managed identity and a developer machine falls back to the local Azure CLI or
        /// Visual Studio sign-in. The identity needs Storage Blob Data Contributor on the container,
        /// and Key Vault Crypto User on the key when the optional key-vault step is configured.
        /// </para>
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        public TBuilder AddCommonDataProtection()
        {
            var blobStorageUri = builder.Configuration["DataProtection:BlobStorageUri"];

            // Gate 1 (blob persistence). Absent means "do nothing": a developer machine, a test host,
            // and the Helpdesk seed all run single-process, where the in-memory default is correct and
            // an unconditional Azure dependency at startup would be a liability, not a feature.
            if (string.IsNullOrWhiteSpace(blobStorageUri))
            {
                return builder;
            }

            var applicationName = builder.Configuration["DataProtection:ApplicationName"]
                ?? builder.Environment.ApplicationName;

            // One credential instance for both sinks so they share a single token cache.
            var credential = new DefaultAzureCredential();

            var dataProtection = builder.Services.AddDataProtection()
                .SetApplicationName(applicationName)
                .PersistKeysToAzureBlobStorage(new Uri(blobStorageUri), credential);

            // Gate 2 (encrypt the key ring at rest) is DELIBERATELY separate from gate 1, not folded
            // into it. Blob persistence is the part that fixes cross-replica cookie and antiforgery
            // decryption, and it has to work on its own, WITHOUT the Key Vault Crypto User role,
            // because that role assignment is granted out of band and can lag the deployment.
            // Encrypting the key ring at rest is an independent hardening step. Coupling the two
            // would mean a missing or delayed role assignment breaks authentication entirely, turning
            // an optional hardening gap into a total outage.
            var keyVaultKeyUri = builder.Configuration["DataProtection:KeyVaultKeyUri"];
            if (!string.IsNullOrWhiteSpace(keyVaultKeyUri))
            {
                dataProtection.ProtectKeysWithAzureKeyVault(new Uri(keyVaultKeyUri), credential);
            }

            return builder;
        }
    }
}
