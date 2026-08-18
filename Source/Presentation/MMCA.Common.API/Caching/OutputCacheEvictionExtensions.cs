using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.IntegrationEvents;

namespace MMCA.Common.API.Caching;

/// <summary>
/// Registration extension for <see cref="OutputCacheEvictionHandler"/>: the DI half of the
/// cross-service output-cache eviction path. The broker half is
/// <c>RegisterOutputCacheEvictionConsumer()</c> on the MassTransit bus configurator; a host that
/// wants the behaviour calls both.
/// </summary>
public static class OutputCacheEvictionExtensions
{
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
