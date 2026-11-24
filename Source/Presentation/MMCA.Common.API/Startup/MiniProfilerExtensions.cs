using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Configures MiniProfiler service registration.
/// </summary>
public static class MiniProfilerExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds MiniProfiler services with Entity Framework profiling when enabled in settings.
        /// <para>
        /// The profiler's result routes (under <c>/profiler</c>: one result, and the list of recent
        /// results) answer only in the Development environment; anywhere else they return 401,
        /// because a profile shows SQL text and timings. A host that wants them elsewhere sets
        /// <c>MiniProfilerOptions.ResultsAuthorize</c> and <c>ResultsListAuthorize</c> itself with
        /// <c>services.Configure&lt;MiniProfilerOptions&gt;(...)</c> after this call, for example to
        /// an authenticated caller in its own admin role.
        /// </para>
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
                    options.ResultsAuthorize = IsDevelopment;
                    options.ResultsListAuthorize = IsDevelopment;
                }).AddEntityFramework();
            }

            return services;
        }
    }

    /// <summary>Whether the request is served by a host running in the Development environment.</summary>
    private static bool IsDevelopment(HttpRequest request) =>
        request.HttpContext.RequestServices.GetService<IHostEnvironment>()?.IsDevelopment() == true;
}
