using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.UI.Services.Diagnostics;

/// <summary>
/// Registration of the opt-in in-app diagnostics log: an in-memory ring-buffer logger plus the
/// buffer the <c>InAppDiagnosticsLog</c> component reads. Nothing is registered unless the host
/// calls <see cref="AddInAppDiagnostics"/>.
/// </summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds the in-app diagnostics log: an <see cref="ILoggerProvider"/> that keeps the last
        /// <see cref="InAppDiagnosticsOptions.Capacity"/> entries (500 by default) at or above
        /// <see cref="InAppDiagnosticsOptions.MinimumLevel"/> (Information by default) in memory, and
        /// the <see cref="InAppLogBuffer"/> the <c>InAppDiagnosticsLog</c> component shows. Meant for
        /// MAUI store builds, which otherwise log nowhere; it works on any Blazor head. The log never
        /// leaves the process unless the user copies or shares it. Idempotent.
        /// </summary>
        /// <param name="configure">Optional options callback (capacity, minimum level).</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddInAppDiagnostics(Action<InAppDiagnosticsOptions>? configure = null)
        {
            var options = services.AddOptions<InAppDiagnosticsOptions>().ValidateDataAnnotations();
            if (configure is not null)
            {
                options.Configure(configure);
            }

            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<InAppLogBuffer>();

            // The logger factory takes every ILoggerProvider registered in the container, which is
            // all ILoggingBuilder.AddProvider does; TryAddEnumerable keeps a repeated call from
            // recording each entry twice.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, InAppLoggerProvider>());

            return services;
        }
    }
}
