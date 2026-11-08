using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;
using MMCA.Common.Application.FeatureFlags;
using MMCA.Common.Infrastructure.FeatureFlags;
using MMCA.Common.Infrastructure.Persistence.FeatureFlags;

namespace MMCA.Common.Infrastructure;

public static partial class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the opt-in reloadable feature flags (ADR-031): stored overrides of the declared
        /// flags, layered over the configuration-backed definitions, so a kill switch flips without a
        /// new revision.
        /// </summary>
        /// <param name="configuration">Application configuration for binding the <c>FeatureFlags</c> section.</param>
        /// <param name="flagAssemblies">
        /// The assemblies whose <c>*Features</c> classes declare the flags this service may override
        /// (typically each module's <c>*.Shared</c> assembly). Only those names can carry an override.
        /// </param>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// <b>Call it AFTER <c>AddAPI()</c></b> (or <c>AddFeatureManagement()</c>). It decorates the
        /// registered <see cref="IFeatureDefinitionProvider"/>, so it throws when there is none yet
        /// rather than silently registering a provider the later call would ignore. The FeatureGate
        /// decorators are unchanged: they keep reading <see cref="IFeatureManager"/>.
        /// </para>
        /// <para>
        /// <b>This call also maps the table.</b> It registers the model gate <c>ApplicationDbContext</c>
        /// reads, so <c>FeatureFlagOverrides</c> is mapped in the one database
        /// <c>FeatureFlags:DataSourceName</c> names (the owning service's own) and every other model
        /// stays byte-identical; <c>DesignTimeDbContextOptions.EnableFeatureFlagOverrides</c> opens the
        /// same gate for <c>dotnet ef</c>. A host that never calls this changes nothing.
        /// </para>
        /// <para>
        /// Calling it again (one call per module) only adds that call's flag assemblies.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">No <see cref="IFeatureDefinitionProvider"/> is registered yet.</exception>
        public IServiceCollection AddReloadableFeatureFlags(IConfiguration configuration, params Assembly[] flagAssemblies)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(flagAssemblies);

            foreach (var assembly in flagAssemblies)
            {
                services.AddSingleton(new FeatureFlagAssembly(assembly));
            }

            // The gate doubles as the "already registered" marker: decorating twice would layer the
            // stored overrides over themselves.
            if (services.Any(descriptor => descriptor.ServiceType == typeof(FeatureFlagOverrideModelGate)))
            {
                return services;
            }

            services.AddOptions<FeatureFlagOverrideSettings>()
                .Bind(configuration.GetSection(FeatureFlagOverrideSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // The model gate: its presence IS the opt-in the context reads (see the type's remarks).
            services.AddSingleton<FeatureFlagOverrideModelGate>();

            services.TryAddSingleton<FeatureFlagCatalog>();
            services.TryAddScoped<IFeatureFlagOverrideStore, EFFeatureFlagOverrideStore>();
            services.TryAddSingleton<IFeatureFlagOverrideSnapshot, FeatureFlagOverrideSnapshot>();
            services.TryAddScoped<IFeatureFlagAdministrationService, FeatureFlagAdministrationService>();

            if (!services.TryDecorate<IFeatureDefinitionProvider>((configured, provider) =>
                    new StoredOverrideFeatureDefinitionProvider(
                        configured,
                        provider.GetRequiredService<IFeatureFlagOverrideSnapshot>(),
                        provider.GetRequiredService<ILogger<StoredOverrideFeatureDefinitionProvider>>())))
            {
                throw new InvalidOperationException(
                    "AddReloadableFeatureFlags layers stored overrides over the registered IFeatureDefinitionProvider, " +
                    "and none is registered yet. Call it after AddAPI() (or AddFeatureManagement()).");
            }

            return services;
        }
    }
}
