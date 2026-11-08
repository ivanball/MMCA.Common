using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Passkeys;

namespace MMCA.Common.Passkeys;

/// <summary>Registration of opt-in passkey sign-in (WebAuthn, ADR-139).</summary>
public static class PasskeysServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers passkey sign-in: the <c>Passkeys</c> settings (validated at startup, a missing value
        /// failing with its key named), the Fido2-backed <see cref="IPasskeyCeremony"/>, and the
        /// <see cref="IPasskeyService"/> that runs the ceremonies.
        /// </summary>
        /// <param name="configuration">Application configuration for binding the settings section.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// Calling this alone turns nothing on: with <c>Passkeys:Enabled</c> false every passkey endpoint
        /// answers 404 and no context maps the <c>PasskeyCredentials</c> table. A host that never calls it
        /// (or never references this package) behaves the same way, needs no migration and carries no
        /// WebAuthn library.
        /// </para>
        /// <para>
        /// Turning it on also needs a controller deriving from <c>PasskeysControllerBase</c> routed at
        /// <c>auth/passkeys</c>, the <c>PasskeyCredentials</c> migration on the Identity database
        /// (<c>DesignTimeDbContextOptions.EnablePasskeys</c>), and an app <c>AuthenticationService</c>
        /// deriving from <c>AuthenticationServiceBase</c>. The passkey store comes from
        /// <c>AddInfrastructure</c>, and the challenges need the shared cache and the distributed lock
        /// <c>AddCaching</c> registers.
        /// </para>
        /// </remarks>
        public IServiceCollection AddPasskeys(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            services.AddOptions<PasskeySettings>()
                .Bind(configuration.GetSection(PasskeySettings.SectionName))
                .ValidateOnStart();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PasskeySettings>, PasskeySettingsValidator>());

            // Singleton: the verifier holds only the bound relying-party settings.
            services.TryAddSingleton<IPasskeyCeremony, Fido2PasskeyCeremony>();

            // Scoped: the service reads the request's passkey store and signs in through the app's scoped
            // authentication service.
            services.TryAddScoped<PasskeyChallenges>();
            services.TryAddScoped<IPasskeyService, PasskeyService>();
            services.TryAddSingleton(TimeProvider.System);

            return services;
        }
    }
}
