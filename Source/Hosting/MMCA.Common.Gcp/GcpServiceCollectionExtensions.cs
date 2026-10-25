using System.Diagnostics.CodeAnalysis;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Gcp.Storage;

namespace MMCA.Common.Gcp;

/// <summary>
/// Service registrations for the Google Cloud implementations of the framework's cloud-neutral
/// abstractions. Each call swaps one inert core default for its Google Cloud implementation, and each
/// is a no-op when its configuration is incomplete, so hosts register them unconditionally and
/// deployments switch the capability on by configuration alone.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class GcpServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers Google Cloud Storage as the <see cref="IFileStorageService"/> (ADR-045), replacing
        /// the unconfigured core default. Reads the <c>FileStorage</c> section, the one the Azure and
        /// S3 adapters read, through keys of its own: the required <c>GcsBucket</c>;
        /// <c>GcsPublicBaseUrl</c> (a CDN or load balancer domain the returned URLs are built on);
        /// <c>GcsServiceUrl</c> (an endpoint override such as the fake-gcs-server emulator, called
        /// without credentials when it is plain HTTP); and <c>GcsProjectId</c> (the quota project for
        /// user credentials). The client authenticates with Application Default Credentials; there
        /// are no HMAC keys. A section without a <c>GcsBucket</c> makes this a no-op, even when it
        /// carries the S3 <c>Bucket</c> or the Azure <c>ContainerName</c>, so hosts register it
        /// unconditionally. A <see cref="StorageClient"/> the host registered first is used as is
        /// (the same TryAdd posture as the Azure and S3 adapters), so it must point at the same
        /// endpoint. The identity needs <c>storage.objects.create</c> and <c>storage.objects.delete</c>
        /// on the bucket, the second also for overwriting an existing object
        /// (<c>roles/storage.objectUser</c> covers both).
        /// </summary>
        /// <param name="configuration">Application configuration providing the <c>FileStorage</c> section.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddGoogleCloudStorage(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            services.AddOptions<GcsFileStorageSettings>()
                .Bind(configuration.GetSection(GcsFileStorageSettings.SectionName));

            var settings = configuration.GetSection(GcsFileStorageSettings.SectionName).Get<GcsFileStorageSettings>();
            if (settings is null || string.IsNullOrWhiteSpace(settings.GcsBucket))
            {
                return services;
            }

            services.TryAddSingleton(_ => GcsStorageClientFactory.Create(settings));
            services.AddTransient<IFileStorageService, GcsFileStorageService>();

            return services;
        }
    }
}
