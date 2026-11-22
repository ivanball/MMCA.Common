using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Services.Auth.TwoFactor;

namespace MMCA.Common.UI.TwoFactor;

/// <summary>
/// Two-factor UI registration (ADR-116): the account page at <see cref="TwoFactorUIModule.AccountPath"/>,
/// its navigation item, and the listener that sends a session the API refused for want of a second
/// factor to that page. Opt-in: a host that does not call it renders and behaves exactly as before.
/// </summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the two-factor client (<see cref="ITwoFactorUIService"/>) and the
        /// <see cref="TwoFactorUIModule"/>, which makes the account page routable and contributes its nav
        /// item and the listener. Call it next to <c>AddUIShared</c> in every UI host (web server,
        /// WebAssembly client, MAUI) whose API hosts the <c>auth/two-factor</c> endpoints.
        /// </summary>
        /// <returns>The service collection, for chaining.</returns>
        public IServiceCollection AddTwoFactorUI()
        {
            // TryAdd, so an app (or the gallery) that registered its own client keeps it.
            services.TryAddScoped<ITwoFactorUIService, TwoFactorUIService>();

            // TryAddEnumerable, so calling this from two places registers the module once (a second
            // module would render the nav item and the listener twice).
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IUIModule, TwoFactorUIModule>());

            return services;
        }
    }
}
