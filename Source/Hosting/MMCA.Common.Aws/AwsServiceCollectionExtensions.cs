using System.Diagnostics.CodeAnalysis;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Aws.Messaging;
using MMCA.Common.Aws.Storage;
using MMCA.Common.Infrastructure;

namespace MMCA.Common.Aws;

/// <summary>
/// Service registrations for the AWS (and S3-compatible) implementations of the framework's
/// cloud-neutral abstractions. Each call swaps one inert core default for its AWS implementation, and
/// each is a no-op when its configuration is incomplete, so hosts register them unconditionally and
/// deployments switch the capability on by configuration alone.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class AwsServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers S3-compatible object storage as the <see cref="IFileStorageService"/> (ADR-045),
        /// replacing the unconfigured core default: AWS S3, Cloudflare R2, MinIO and the GCS XML API.
        /// Reads the <c>FileStorage</c> section: the required <c>Bucket</c>; <c>Region</c> (AWS) or
        /// <c>ServiceUrl</c> (an S3-compatible endpoint, with <c>Region</c> as its signing region);
        /// <c>ForcePathStyle</c> (MinIO, LocalStack); <c>PublicBaseUrl</c> (a CDN or public bucket
        /// domain the returned URLs are built on); <c>AccessKeyId</c> + <c>SecretAccessKey</c> (else
        /// the AWS default credential chain); and <c>RequestChecksumCalculation</c> /
        /// <c>ResponseChecksumValidation</c>, both defaulting to <c>WHEN_REQUIRED</c> because the AWS
        /// SDK v4 sends integrity checksums by default and several S3-compatible services reject them.
        /// A section without a <c>Bucket</c> makes this a no-op, so hosts register it unconditionally.
        /// An <see cref="IAmazonS3"/> the host registered first is used as is (the same TryAdd
        /// posture as the Azure adapter's container client), so it must point at the same endpoint.
        /// </summary>
        /// <param name="configuration">Application configuration providing the <c>FileStorage</c> section.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="InvalidOperationException">Only one of <c>AccessKeyId</c> / <c>SecretAccessKey</c> is set.</exception>
        public IServiceCollection AddS3FileStorage(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            services.AddOptions<S3FileStorageSettings>()
                .Bind(configuration.GetSection(S3FileStorageSettings.SectionName));

            var settings = configuration.GetSection(S3FileStorageSettings.SectionName).Get<S3FileStorageSettings>();
            if (settings is null || string.IsNullOrWhiteSpace(settings.Bucket))
            {
                return services;
            }

            // A half-configured key pair fails here, at registration, rather than on the first upload.
            _ = AwsStaticCredentials.Resolve(settings.AccessKeyId, settings.SecretAccessKey, S3StorageClientFactory.SectionPath);

            services.TryAddSingleton(_ => S3StorageClientFactory.Create(settings));
            services.AddTransient<IFileStorageService, S3FileStorageService>();

            return services;
        }

        /// <summary>
        /// Registers the Amazon SQS/SNS broker transport, so <c>AddBrokerMessaging</c> can select it
        /// with <c>MessageBus:Provider=AmazonSqs</c>. Call it BEFORE <c>AddBrokerMessaging</c>, which
        /// picks the transport while it registers the bus. Reads <c>MessageBus:AmazonSqs</c>:
        /// <c>Region</c> (else the AWS SDK fallback chain), <c>ServiceUrl</c> (an endpoint override for
        /// both SQS and SNS, e.g. LocalStack) and <c>AccessKeyId</c> + <c>SecretAccessKey</c> (else the
        /// AWS default credential chain). The broker connection string is not used. Every
        /// <c>MessageBus:RedeliveryIntervalsSeconds</c> value must be 900 or less (the SQS delay cap),
        /// so the framework default <c>[60, 600, 3600]</c> needs overriding; <c>AddBrokerMessaging</c>
        /// throws naming the interval otherwise. Registration only: a host whose provider is
        /// something else is unaffected.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddAmazonSqsTransport() =>
            services.AddBrokerTransport(new AmazonSqsTransport());
    }
}
