using Amazon.S3;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Aws.DataProtection;

namespace MMCA.Common.Aws;

/// <summary>
/// Registration extension for a shared ASP.NET Core DataProtection key ring on AWS.
/// <para>
/// The framework default keeps the key ring in memory, which is correct for a single-process host
/// and wrong for a scaled-out one: every replica generates its own keys, so an auth cookie or an
/// antiforgery token minted by replica A cannot be decrypted by replica B. The user sees random
/// sign-outs and "The antiforgery token could not be decrypted" errors that follow no pattern
/// because they follow the load balancer. Persisting the key ring to a single S3 object gives every
/// replica the same keys, which is what makes cookies and tokens portable across the deployment.
/// </para>
/// </summary>
public static class AwsDataProtectionExtensions
{
    private const string ObjectUriKey = "DataProtection:S3ObjectUri";
    private const string KmsKeyIdKey = "DataProtection:KmsKeyId";

    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        /// <summary>
        /// Persists the DataProtection key ring to one Amazon S3 object so that every replica of a
        /// scaled-out host shares one key ring, optionally encrypted at rest under a KMS key.
        /// <para>
        /// Configuration keys:
        /// <list type="bullet">
        ///   <item><c>DataProtection:S3ObjectUri</c> (the gate): the object that holds the key ring
        ///     XML, as <c>s3://bucket/path/keys.xml</c>. When absent or whitespace this method does
        ///     nothing at all, so local development and tests keep the in-memory default and take no
        ///     AWS dependency at startup. The bucket is provisioned by infrastructure; the object is
        ///     created on the first key.</item>
        ///   <item><c>DataProtection:KmsKeyId</c> (optional): the KMS key (an id, ARN or alias) every
        ///     write asks S3 to encrypt the object under (SSE-KMS). When absent the bucket's default
        ///     encryption applies.</item>
        ///   <item><c>DataProtection:ApplicationName</c> (optional): the application discriminator
        ///     that isolates one application's keys from another's when several share a key ring.
        ///     Defaults to the host application name, as in the Azure and Google Cloud siblings.</item>
        /// </list>
        /// </para>
        /// <para>
        /// The object is one document rewritten under an S3 conditional write (<c>If-Match</c> on
        /// the ETag it read, <c>If-None-Match</c> to create it), so two replicas creating a key at the
        /// same moment cannot overwrite each other: the loser re-reads and retries. The S3 client is
        /// the <see cref="IAmazonS3"/> the host registered (an emulator endpoint is set there, or it
        /// is the one <c>AddS3FileStorage</c> registers); otherwise one is built from the AWS SDK
        /// defaults (region and credentials from the default chain). The identity needs
        /// <c>s3:GetObject</c> and <c>s3:PutObject</c> on the object, plus <c>s3:ListBucket</c> on the
        /// bucket (without it S3 answers a not-yet-created object with 403 rather than 404, and the
        /// first start fails), plus <c>kms:GenerateDataKey</c> and <c>kms:Decrypt</c> on a
        /// customer-managed key when one is configured.
        /// </para>
        /// <para>
        /// A host keeps exactly one key ring: setting this object and
        /// <c>DataProtection:BlobStorageUri</c> or <c>DataProtection:GcsObjectUri</c> together fails
        /// the startup tripwire.
        /// </para>
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// <c>DataProtection:S3ObjectUri</c> is not of the <c>s3://bucket/key</c> form.
        /// </exception>
        public TBuilder AddAwsDataProtection()
        {
            var objectUri = builder.Configuration[ObjectUriKey];

            // The gate. Absent means "do nothing": a developer machine and a test host run
            // single-process, where the in-memory default is correct and an unconditional AWS
            // dependency at startup would be a liability, not a feature. No receipt either, so an
            // object that only appears later still trips the startup check.
            if (string.IsNullOrWhiteSpace(objectUri))
            {
                return builder;
            }

            // Fails here, naming the key, before anything is registered.
            var location = S3ObjectLocation.Parse(objectUri, ObjectUriKey);

            // The receipt the core startup tripwire checks: DataProtection:S3ObjectUri set without
            // this call fails the host instead of running every replica on its own key ring.
            builder.Services.AddSingleton(new HostIntegrationMarker(HostIntegrationNames.AwsDataProtection));

            var applicationName = builder.Configuration["DataProtection:ApplicationName"]
                ?? builder.Environment.ApplicationName;

            builder.Services.AddDataProtection()
                .SetApplicationName(applicationName)
                .PersistKeysToAmazonS3(location, builder.Configuration[KmsKeyIdKey]);

            return builder;
        }
    }
}
