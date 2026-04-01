using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Configures MiniProfiler service registration and middleware.
/// </summary>
public static class MiniProfilerExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds MiniProfiler services with Entity Framework profiling when enabled in settings.
        /// </summary>
        public IServiceCollection AddMiniProfilerIfEnabled(ApplicationSettings applicationSettings)
        {
            if (applicationSettings.UseMiniProfiler)
            {
                services.AddMiniProfiler(options =>
                {
                    options.RouteBasePath = "/profiler";
                    options.PopupShowTimeWithChildren = true;
                    options.ColorScheme = StackExchange.Profiling.ColorScheme.Dark;
                }).AddEntityFramework();
            }

            return services;
        }
    }
}
