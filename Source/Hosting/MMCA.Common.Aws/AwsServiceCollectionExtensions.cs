using System.Diagnostics.CodeAnalysis;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.S3;
using Amazon.SimpleNotificationService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Aws.Messaging;
using MMCA.Common.Aws.Notifications;
using MMCA.Common.Aws.Rds;
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
        /// AWS default credential chain). The broker connection string is not used. An unconfigured
        /// <c>MessageBus:RedeliveryIntervalsSeconds</c> defaults to <c>[60, 300, 900]</c> on this
        /// transport; a configured value over 900 (the SQS delay cap) makes <c>AddBrokerMessaging</c>
        /// throw naming the interval. Registration only: a host whose provider is something else is
        /// unaffected.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddAmazonSqsTransport() =>
            services.AddBrokerTransport(new AmazonSqsTransport());

        /// <summary>
        /// Switches every PostgreSQL connection onto RDS IAM database authentication (Amazon RDS and
        /// Aurora PostgreSQL): the password becomes a short-lived token signed locally for the
        /// connection string's own host, port and user, refreshed every 10 minutes (30 seconds after a
        /// failure) through the core data-source configurator, so the contexts and the readiness check
        /// share one data source per connection string. Reads <c>Rds:IamAuthentication</c>:
        /// <c>Enabled</c> (the gate; absent or false makes this a no-op), <c>Region</c> (the database's
        /// region, else the AWS SDK region chain) and <c>AccessKeyId</c> + <c>SecretAccessKey</c> (else
        /// the AWS default credential chain). The connection strings carry no password, and each must
        /// require TLS (<c>SSL Mode=Require</c>, <c>VerifyCA</c> or <c>VerifyFull</c>), because the token
        /// is sent as the password. SQL Server, SQLite and Cosmos sources are untouched: RDS has no IAM
        /// authentication for SQL Server.
        /// </summary>
        /// <param name="configuration">Application configuration providing <c>Rds:IamAuthentication</c> and the connection strings.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// A configured PostgreSQL connection string does not require TLS (the message names its key),
        /// or only one of <c>AccessKeyId</c> / <c>SecretAccessKey</c> is set.
        /// </exception>
        public IServiceCollection AddAwsRdsIamAuthentication(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var settings = configuration.GetSection(RdsIamAuthenticationSettings.SectionPath).Get<RdsIamAuthenticationSettings>();
            if (settings is not { Enabled: true })
            {
                return services;
            }

            var credentials = AwsStaticCredentials.Resolve(
                settings.AccessKeyId,
                settings.SecretAccessKey,
                RdsIamAuthenticationSettings.SectionPath);

            // Deterministic: a connection string already in configuration that would send the token
            // in clear fails here, at registration, not on the first query.
            RdsIamAuthenticationConfigurator.EnsureConfiguredConnectionStringsAreEncrypted(configuration);

            var region = string.IsNullOrWhiteSpace(settings.Region) ? null : RegionEndpoint.GetBySystemName(settings.Region);

            return services.AddNpgsqlDataSourceConfigurator(new RdsIamAuthenticationConfigurator(region, credentials));
        }

        /// <summary>
        /// Registers OS-level native push delivery through SNS mobile push (ADR-044), replacing the core
        /// <c>NullNativePushSender</c> / <c>NullPushDeviceRegistrar</c> pair. SNS keeps no owner per
        /// endpoint, so a DynamoDB table is the installation registry: one item per installation id,
        /// and the ownership rules of <see cref="IPushDeviceRegistrar"/> (handover on a matching push
        /// channel, owner-scoped delete) are conditional writes on it.
        /// <para>
        /// Reads the shared gate <c>NativePush:Enabled</c> and the <c>NativePush:Sns</c> section:
        /// <c>FcmPlatformApplicationArn</c> and/or <c>ApnsPlatformApplicationArn</c> (the SNS platform
        /// applications holding the FCM v1 service account and the APNs key; a registration for a
        /// platform without one fails validation), <c>BroadcastTopicArn</c> (every endpoint subscribes
        /// to it; a broadcast is one publish there), <c>InstallationsTable</c>, <c>Region</c>,
        /// <c>ServiceUrl</c> (one endpoint override for SNS and DynamoDB, e.g. LocalStack) and
        /// <c>AccessKeyId</c> + <c>SecretAccessKey</c> (else the AWS default credential chain). When the
        /// gate is off, or the section lacks the table, the topic or both platform applications, the
        /// call is a no-op, so hosts register it unconditionally and deployments switch the channel on
        /// by configuration alone.
        /// </para>
        /// <para>
        /// Every resource is provisioned outside the application. The table has the partition key
        /// <c>InstallationId</c> (string) and a global secondary index named <c>UserId-index</c> with
        /// the partition key <c>UserId</c> (string) projecting all attributes. The identity needs
        /// <c>sns:CreatePlatformEndpoint</c> on the platform applications, <c>sns:SetEndpointAttributes</c>,
        /// <c>sns:DeleteEndpoint</c> and <c>sns:Publish</c> on their endpoints, <c>sns:Subscribe</c>,
        /// <c>sns:Unsubscribe</c> and <c>sns:Publish</c> on the topic, and <c>dynamodb:GetItem</c>,
        /// <c>PutItem</c>, <c>DeleteItem</c> and <c>Query</c> on the table and its index. An
        /// <see cref="IAmazonSimpleNotificationService"/> or <see cref="IAmazonDynamoDB"/> the host
        /// registered first is used as is.
        /// </para>
        /// </summary>
        /// <param name="configuration">Application configuration providing the <c>NativePush</c> section.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="InvalidOperationException">Only one of <c>AccessKeyId</c> / <c>SecretAccessKey</c> is set.</exception>
        public IServiceCollection AddAmazonSnsPush(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var section = configuration.GetSection(SnsNativePushSettings.SectionPath);
            var settings = section.Get<SnsNativePushSettings>();
            if (!bool.TryParse(configuration[SnsNativePushSettings.EnabledKey], out var enabled)
                || !enabled
                || settings is not { IsComplete: true })
            {
                return services;
            }

            // A half-configured key pair fails here, at registration, rather than on the first send.
            var credentials = AwsStaticCredentials.Resolve(settings.AccessKeyId, settings.SecretAccessKey, SnsNativePushSettings.SectionPath);

            services.AddOptions<SnsNativePushSettings>().Bind(section);
            services.TryAddSingleton(_ => SnsPushClientFactory.CreateSns(settings, credentials));
            services.TryAddSingleton(_ => SnsPushClientFactory.CreateDynamoDb(settings, credentials));
            services.TryAddSingleton<SnsInstallationRegistry>();
            services.AddTransient<INativePushSender, SnsNativePushSender>();
            services.AddTransient<IPushDeviceRegistrar, SnsPushDeviceRegistrar>();

            return services;
        }

    }
}
