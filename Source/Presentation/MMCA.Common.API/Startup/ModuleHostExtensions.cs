using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Modules;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Collapses the settings-bind plus <see cref="ModuleLoader"/> construction every module-hosting
/// service repeats verbatim in its <c>Program.cs</c>.
/// <para>
/// Deliberately narrow: it binds and validates the two settings sections, builds the loader and
/// registers it as a singleton. It does NOT run discovery, add the module health checks, or touch
/// the surrounding registration order. Those stay explicit at the host, because discovery has to sit
/// at a host-chosen position inside the ADR-014 application pipeline and the health checks can only
/// enumerate what discovery has already found.
/// </para>
/// </summary>
public static class ModuleHostExtensions
{
    extension(WebApplicationBuilder builder)
    {
        /// <summary>
        /// Binds <see cref="ApplicationSettings"/> and <see cref="ModulesSettings"/> from
        /// configuration (both validated on start), constructs the host's
        /// <see cref="ModuleLoader"/> and registers it as a singleton.
        /// </summary>
        /// <param name="moduleLoaderLogger">
        /// Optional logger for module-discovery diagnostics. A host that has already bootstrapped a
        /// logger passes one here (for example
        /// <c>MMCA.Common.Aspire.Logging.SerilogHostExtensions.CreateBootstrapLoggerFactory()</c>);
        /// when omitted the loader keeps its own <c>NullLogger</c> default and discovery runs
        /// silently.
        /// </param>
        /// <returns>
        /// The bound settings and the loader, plus the discovery step to register inside the
        /// application pipeline.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// The <c>ApplicationSettings</c> configuration section is absent.
        /// </exception>
        public ModuleHostContext AddModuleHost(ILogger<ModuleLoader>? moduleLoaderLogger = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            var services = builder.Services;
            var configuration = builder.Configuration;

            services.AddOptions<ApplicationSettings>()
                .Bind(configuration.GetSection(ApplicationSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            var applicationSettings = configuration.GetSection(ApplicationSettings.SectionName).Get<ApplicationSettings>()
                ?? throw new InvalidOperationException("ApplicationSettings section is not configured.");

            services.AddOptions<ModulesSettings>()
                .Bind(configuration.GetSection(ModulesSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            var modulesSettings = configuration
                .GetSection(ModulesSettings.SectionName)
                .Get<ModulesSettings>() ?? [];

            var moduleLoader = moduleLoaderLogger is null
                ? new ModuleLoader()
                : new ModuleLoader { Logger = moduleLoaderLogger };

            services.AddSingleton(moduleLoader);

            return new ModuleHostContext(
                configuration,
                builder.Environment.EnvironmentName,
                applicationSettings,
                modulesSettings,
                moduleLoader);
        }
    }
}
