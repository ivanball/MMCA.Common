using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Testing.Support;

/// <summary>
/// Test helpers that swap a host's registration for a fake inside a test factory's
/// <c>ConfigureTestServices</c>.
/// </summary>
public static class ServiceCollectionFakeExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Removes every unkeyed registration of <typeparamref name="TService"/> and registers
        /// <typeparamref name="TFake"/> in its place, with the lifetime of the LAST registration (the one
        /// DI would have resolved). Keyed registrations are neither removed nor counted.
        /// </summary>
        /// <typeparam name="TService">The service the host registered.</typeparam>
        /// <typeparam name="TFake">The fake implementation.</typeparam>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// The host never registered <typeparamref name="TService"/> (a stale fake); the collection is unchanged.
        /// </exception>
        public IServiceCollection ReplaceWithFake<TService, TFake>()
            where TService : class
            where TFake : class, TService
        {
            var lifetime = LastUnkeyedRegistration(services, typeof(TService)).Lifetime;
            return Swap(services, ServiceDescriptor.Describe(typeof(TService), typeof(TFake), lifetime));
        }

        /// <summary>
        /// Removes every unkeyed registration of <typeparamref name="TService"/> and registers
        /// <typeparamref name="TFake"/> in its place with the given lifetime. Keyed registrations are
        /// neither removed nor counted.
        /// </summary>
        /// <typeparam name="TService">The service the host registered.</typeparam>
        /// <typeparam name="TFake">The fake implementation.</typeparam>
        /// <param name="lifetime">The lifetime the fake is registered with.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// The host never registered <typeparamref name="TService"/> (a stale fake); the collection is unchanged.
        /// </exception>
        public IServiceCollection ReplaceWithFake<TService, TFake>(ServiceLifetime lifetime)
            where TService : class
            where TFake : class, TService
        {
            _ = LastUnkeyedRegistration(services, typeof(TService));
            return Swap(services, ServiceDescriptor.Describe(typeof(TService), typeof(TFake), lifetime));
        }

        /// <summary>
        /// Removes every unkeyed registration of <typeparamref name="TService"/> and registers
        /// <paramref name="instance"/> in its place as a singleton. Keyed registrations are neither
        /// removed nor counted.
        /// </summary>
        /// <typeparam name="TService">The service the host registered.</typeparam>
        /// <param name="instance">The fake instance.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="instance"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// The host never registered <typeparamref name="TService"/> (a stale fake); the collection is unchanged.
        /// </exception>
        public IServiceCollection ReplaceWithFake<TService>(TService instance)
            where TService : class
        {
            ArgumentNullException.ThrowIfNull(instance);

            _ = LastUnkeyedRegistration(services, typeof(TService));
            return Swap(services, ServiceDescriptor.Singleton(instance));
        }
    }

    private static ServiceDescriptor LastUnkeyedRegistration(IServiceCollection services, Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(services);

        ServiceDescriptor? last = null;
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == serviceType && !descriptor.IsKeyedService)
            {
                last = descriptor;
            }
        }

        return last ?? throw new InvalidOperationException(
            $"Cannot replace '{serviceType.FullName}' with a fake: the host never registered it. "
            + "A fake for a service the host does not register is stale; fix or remove the fake.");
    }

    private static IServiceCollection Swap(IServiceCollection services, ServiceDescriptor fake)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == fake.ServiceType && !descriptor.IsKeyedService)
            {
                services.RemoveAt(i);
            }
        }

        services.Add(fake);
        return services;
    }
}
