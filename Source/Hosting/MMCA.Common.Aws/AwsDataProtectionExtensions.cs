using Amazon.SimpleSystemsManagement;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Integrations;

namespace MMCA.Common.Aws;

/// <summary>
/// Registration extension for a shared ASP.NET Core DataProtection key ring on AWS.
/// <para>
/// The framework default keeps the key ring in memory, which is correct for a single-process host
/// and wrong for a scaled-out one: every replica generates its own keys, so an auth cookie or an
/// antiforgery token minted by replica A cannot be decrypted by replica B. The user sees random
/// sign-outs and "The antiforgery token could not be decrypted" errors that follow no pattern
/// because they follow the load balancer. Persisting the key ring to Systems Manager Parameter Store
/// gives every replica the same keys, which is what makes cookies and tokens portable across the
/// deployment.
/// </para>
/// </summary>
public static class AwsDataProtectionExtensions
{
    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        /// <summary>
        /// Persists the DataProtection key ring to AWS Systems Manager Parameter Store so that every
        /// replica of a scaled-out host shares one key ring. Each key is one SecureString parameter
        /// under the prefix, so it is encrypted at rest by KMS with no separate key-protection step.
        /// <para>
        /// Configuration keys:
        /// <list type="bullet">
        ///   <item><c>DataProtection:SsmParameterPrefix</c> (the gate): the parameter path that holds
        ///     the key ring and nothing else, e.g. <c>/adc/prod/dataprotection</c>. When absent or
        ///     whitespace this method does nothing at all, so local development and tests keep the
        ///     in-memory default and take no AWS dependency at startup.</item>
        ///   <item><c>DataProtection:KmsKeyId</c> (optional): the KMS key that encrypts the
        ///     parameters (an id, ARN or alias). Defaults to the account's <c>aws/ssm</c> key.</item>
        ///   <item><c>DataProtection:ApplicationName</c> (optional): the application discriminator
        ///     that isolates one application's keys from another's when several share a key ring.
        ///     Defaults to the host application name, as in the Azure sibling.</item>
        /// </list>
        /// </para>
        /// <para>
        /// The Systems Manager client is the <see cref="IAmazonSimpleSystemsManagement"/> the host
        /// registered first, used as is (an emulator endpoint or a non-default credential is set
        /// there); otherwise the package builds one from the AWS SDK defaults (region and credentials
        /// from the default chain, or the <c>AWS</c> configuration section). The identity needs
        /// <c>ssm:GetParametersByPath</c> and <c>ssm:PutParameter</c> on the prefix, plus <c>kms:Encrypt</c> and <c>kms:Decrypt</c> on a customer-managed key.
        /// </para>
        /// <para>
        /// A host keeps exactly one key ring: setting this prefix and
        /// <c>DataProtection:BlobStorageUri</c> together fails the startup tripwire.
        /// </para>
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        public TBuilder AddAwsDataProtection()
        {
            var parameterPrefix = builder.Configuration["DataProtection:SsmParameterPrefix"];

            // The gate. Absent means "do nothing": a developer machine and a test host run
            // single-process, where the in-memory default is correct and an unconditional AWS
            // dependency at startup would be a liability, not a feature. No receipt either, so a
            // prefix that only appears later still trips the startup check.
            if (string.IsNullOrWhiteSpace(parameterPrefix))
            {
                return builder;
            }

            // The receipt the core startup tripwire checks: DataProtection:SsmParameterPrefix set
            // without this call fails the host instead of running every replica on its own key ring.
            builder.Services.AddSingleton(new HostIntegrationMarker(HostIntegrationNames.AwsDataProtection));

            var applicationName = builder.Configuration["DataProtection:ApplicationName"]
                ?? builder.Environment.ApplicationName;
            var kmsKeyId = builder.Configuration["DataProtection:KmsKeyId"];

            builder.Services.AddDataProtection()
                .SetApplicationName(applicationName)
                .PersistKeysToAWSSystemsManager(parameterPrefix, options =>
                {
                    if (!string.IsNullOrWhiteSpace(kmsKeyId))
                    {
                        options.KMSKeyId = kmsKeyId;
                    }
                });

            return builder;
        }
    }
}
