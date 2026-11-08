using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.MachineClients;
using MMCA.Common.Infrastructure.Auth.MachineClients;
using MMCA.Common.Infrastructure.Persistence.MachineClients;

namespace MMCA.Common.Infrastructure;

public static partial class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers machine clients (OAuth client credentials, ADR-140): the <c>MachineClients</c>
        /// settings (validated at startup), the EF client store and the
        /// <see cref="IMachineClientService"/> behind the token and administration endpoints.
        /// </summary>
        /// <param name="configuration">Application configuration for binding the settings section.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// Calling this alone turns nothing on: with <c>MachineClients:Enabled</c> false every machine
        /// client endpoint answers 404 and no context maps the <c>MachineClients</c> table. A host that
        /// never calls it behaves the same way and needs no migration.
        /// </para>
        /// <para>
        /// Turning it on also needs the app's <see cref="IServiceAccountProvisioner"/> (startup fails
        /// without one), controllers deriving from <c>MachineTokenControllerBase</c> (routed at
        /// <c>auth/token</c>) and <c>MachineClientsControllerBase</c>, and the <c>MachineClients</c>
        /// migration on the Identity database (<c>DesignTimeDbContextOptions.EnableMachineClients</c>).
        /// The app's <c>User</c> answers <c>IAuthUser.IsServiceAccount</c> for the accounts the
        /// provisioner creates, which is what keeps them off every interactive sign-in path.
        /// </para>
        /// </remarks>
        public IServiceCollection AddMachineClients(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            services.AddOptions<MachineClientSettings>()
                .Bind(configuration.GetSection(MachineClientSettings.SectionName))
                .ValidateOnStart();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<MachineClientSettings>, MachineClientSettingsValidator>());

            // Scoped, sharing the request's Identity context with the provisioner the app registers.
            services.TryAddScoped<IMachineClientStore, EFMachineClientStore>();
            services.TryAddScoped<IMachineClientService, MachineClientService>();
            services.TryAddSingleton(TimeProvider.System);

            return services;
        }
    }
}
