using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI;

/// <summary>
/// Registers services shared across all UI hosts (Blazor Server, WebAssembly, MAUI).
/// Uses C# preview extension types to add <c>AddUIShared</c> directly to <see cref="IServiceCollection"/>.
/// </summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers shared UI infrastructure: API settings, named <c>"APIClient"</c> HttpClient with
        /// JWT auth handler, authentication service, and cart state service.
        /// </summary>
        public IServiceCollection AddUIShared(IConfiguration configuration)
        {
            // Bind and validate API settings at startup to fail fast on missing configuration
            services.AddOptions<ApiSettings>()
                .Bind(configuration.GetSection(ApiSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Bind layout settings (footer text, etc.) — optional, defaults to empty values
            services.AddOptions<LayoutSettings>()
                .Bind(configuration.GetSection(LayoutSettings.SectionName));

            // Auth handler injects Bearer token into every outgoing API request
            services.AddTransient<AuthDelegatingHandler>();

            // Named HttpClient used by all EntityServiceBase-derived services
            services.AddHttpClient("APIClient", (serviceProvider, client) =>
            {
                var apiSettings = serviceProvider.GetRequiredService<IOptions<ApiSettings>>().Value;
                if (string.IsNullOrWhiteSpace(apiSettings.ApiEndpoint))
                {
                    throw new InvalidOperationException("ApiEndpoint is required and cannot be null or empty.");
                }

                client.BaseAddress = new Uri(apiSettings.ApiEndpoint, UriKind.Absolute);
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            }).AddHttpMessageHandler<AuthDelegatingHandler>();

            // TryAdd prevents duplicate registration when called from multiple hosts
            services.TryAddScoped<IAuthUIService, AuthUIService>();
            services.TryAddScoped<ListPageStateService>();

            // Default no-op OAuth settings — downstream apps override with TryAdd before this runs,
            // or replace after by calling AddSingleton<IOAuthUISettings, ConcreteSettings>()
            services.TryAddSingleton<IOAuthUISettings, DefaultOAuthUISettings>();

            return services;
        }
    }
}

/// <summary>Marker class used to reference the UI.Shared assembly (e.g., for Scrutor scanning).</summary>
public class UISharedAssemblyReference { }
