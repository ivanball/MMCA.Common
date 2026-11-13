using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Aspire.Integrations;

namespace MMCA.Common.ServiceHost.Tests.Support;

/// <summary>
/// Reads registration shape off an <see cref="IServiceCollection"/> without building a provider.
/// Keyed descriptors are handled separately because their non-keyed accessors throw.
/// </summary>
internal static class ServiceDescriptorInspection
{
    /// <summary>Whether a non-keyed descriptor for <typeparamref name="TService"/> exists.</summary>
    /// <typeparam name="TService">The service type.</typeparam>
    /// <param name="services">The collection.</param>
    /// <returns><see langword="true"/> when at least one is registered.</returns>
    public static bool HasService<TService>(IServiceCollection services) =>
        services.Any(d => !d.IsKeyedService && d.ServiceType == typeof(TService));

    /// <summary>Whether a non-keyed descriptor whose service type has the given simple name exists.</summary>
    /// <param name="services">The collection.</param>
    /// <param name="serviceTypeName">The service type's <see cref="System.Reflection.MemberInfo.Name"/>.</param>
    /// <returns><see langword="true"/> when at least one is registered.</returns>
    public static bool HasServiceNamed(IServiceCollection services, string serviceTypeName) =>
        services.Any(d => !d.IsKeyedService && string.Equals(d.ServiceType.Name, serviceTypeName, StringComparison.Ordinal));

    /// <summary>
    /// The implementation type of every non-keyed descriptor for <typeparamref name="TService"/>,
    /// read from the implementation type, the instance, or the factory delegate's return type.
    /// </summary>
    /// <typeparam name="TService">The service type.</typeparam>
    /// <param name="services">The collection.</param>
    /// <returns>The implementation types; <see langword="null"/> where none can be read.</returns>
    public static IReadOnlyList<Type?> ImplementationTypesOf<TService>(IServiceCollection services) =>
        [.. services
            .Where(d => !d.IsKeyedService && d.ServiceType == typeof(TService))
            .Select(ImplementationTypeOf)
        ];

    /// <summary>Whether the core's ServiceDefaultsTelemetry receipt (registered by AddServiceDefaults) is present.</summary>
    /// <param name="services">The collection.</param>
    /// <returns><see langword="true"/> when <c>AddServiceDefaults()</c> has already run.</returns>
    public static bool HasServiceDefaultsTelemetryMarker(IServiceCollection services) =>
        services.Any(d => !d.IsKeyedService
            && d.ServiceType == typeof(HostIntegrationMarker)
            && d.ImplementationInstance is HostIntegrationMarker marker
            && string.Equals(marker.Name, HostIntegrationNames.ServiceDefaultsTelemetry, StringComparison.Ordinal));

    private static Type? ImplementationTypeOf(ServiceDescriptor descriptor) =>
        descriptor.ImplementationType
        ?? descriptor.ImplementationInstance?.GetType()
        ?? FactoryReturnType(descriptor.ImplementationFactory);

    // A factory registered through AddSingleton<TService, TImplementation>(Func<IServiceProvider, TImplementation>)
    // keeps its closed delegate type, whose last generic argument is the implementation type.
    private static Type? FactoryReturnType(Delegate? factory) =>
        factory?.GetType() is { IsGenericType: true } delegateType
            ? delegateType.GetGenericArguments()[^1]
            : null;
}
