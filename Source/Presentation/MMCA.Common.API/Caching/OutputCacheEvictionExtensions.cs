using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Services;
using MMCA.Common.Domain.IntegrationEvents;

namespace MMCA.Common.API.Caching;

/// <summary>
/// Registration extension for <see cref="OutputCacheEvictionHandler"/>: the DI half of the
/// cross-service output-cache eviction path. The broker half is
/// <c>RegisterOutputCacheEvictionConsumer()</c> on the MassTransit bus configurator; a host that
/// wants the behaviour calls both.
/// <para>
/// Also carries the multi-tag eviction helpers a mutating controller reaches for after a write: one
/// call naming every tag the write invalidated, instead of a private per-controller helper wrapping
/// a run of <c>EvictByTagAsync</c> calls.
/// </para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with multiple extension(T) blocks in one static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class OutputCacheEvictionExtensions
{
    /// <summary>
    /// Prefix of the best-effort operation name used by <c>TryEvictTagsAsync</c>. The tag is
    /// appended so a failure is attributable to the cache it could not clear; keep call-site tags
    /// low-cardinality literals, because the name becomes a metric tag.
    /// </summary>
    private const string EvictOperationPrefix = "output-cache-evict:";

    extension(IOutputCacheStore store)
    {
        /// <summary>
        /// Evicts every output-cache entry carrying any of <paramref name="tags"/>, in the order
        /// given. Equivalent to a run of <see cref="IOutputCacheStore.EvictByTagAsync"/> calls, which
        /// is what a mutating action needs after a write that invalidates more than one tag.
        /// </summary>
        /// <param name="cancellationToken">Token passed to each eviction.</param>
        /// <param name="tags">The output-cache tags whose entries should be evicted.</param>
        /// <returns>A task that completes when every tag has been evicted.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="store"/> or <paramref name="tags"/> is null.
        /// </exception>
        public async ValueTask EvictTagsAsync(CancellationToken cancellationToken, params string[] tags)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(tags);

            foreach (var tag in tags)
            {
                await store.EvictByTagAsync(tag, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Best-effort sibling of <c>EvictTagsAsync</c>: evicts each tag, and turns a cache-store
        /// failure (a Redis outage, a transient network fault) into one Warning plus one
        /// <see cref="BestEffort"/> metric increment instead of an exception. The mutation this
        /// follows has already committed, so surfacing the failure would turn a successful write into
        /// a client-visible error while leaving the write in place; an entry that could not be
        /// evicted expires on its own TTL.
        /// <para>
        /// Eviction runs under <see cref="CancellationToken.None"/>, not the request token: the write
        /// has committed, so a client that disconnected mid-response must not abandon the cleanup.
        /// </para>
        /// </summary>
        /// <param name="logger">Logger used to report a failed eviction.</param>
        /// <param name="tags">The output-cache tags whose entries should be evicted.</param>
        /// <returns>A task that completes when every tag has been evicted or its failure recorded.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="store"/>, <paramref name="logger"/> or <paramref name="tags"/> is null.
        /// </exception>
        public async ValueTask TryEvictTagsAsync(ILogger logger, params string[] tags)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(tags);

            foreach (var tag in tags)
            {
                await BestEffort.ExecuteAsync(
                    string.Concat(EvictOperationPrefix, tag),
                    logger,
                    ct => store.EvictByTagAsync(tag, ct).AsTask(),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers <see cref="OutputCacheEvictionHandler"/> as an
        /// <see cref="IIntegrationEventHandler{T}"/> for
        /// <see cref="OutputCacheEvictionRequested"/>. Requires <c>AddOutputCache()</c> to have been
        /// called (it supplies the singleton <c>IOutputCacheStore</c> the handler evicts through).
        /// <para>
        /// Registered as a singleton to match the lifetime the module scanner gives every other
        /// integration-event handler, and through
        /// <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable(IServiceCollection, ServiceDescriptor)"/>
        /// so calling it twice (a host plus a module that both want the behaviour) registers one
        /// handler rather than evicting every tag twice.
        /// </para>
        /// </summary>
        /// <returns>The same service collection for chaining.</returns>
        public IServiceCollection AddOutputCacheEvictionHandler()
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddEnumerable(ServiceDescriptor.Singleton<
                IIntegrationEventHandler<OutputCacheEvictionRequested>,
                OutputCacheEvictionHandler>());

            return services;
        }
    }
}
