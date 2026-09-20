using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Application.Auth.EmailConfirmation;
using MMCA.Common.Application.Auth.Permissions;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Auth.Administration;
using MMCA.Common.Infrastructure.Auth.TwoFactor;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.Infrastructure;

public static partial class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the opt-in time-based second factor: the TOTP service, the sign-in challenge, and
        /// the <c>Authentication:TwoFactor</c> settings.
        /// </summary>
        /// <param name="configuration">Application configuration for binding the settings section.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// It deliberately registers no <c>ITwoFactorStore</c>: the account's secret and recovery
        /// hashes belong to the app's own <c>User</c> aggregate, so the consumer supplies that one
        /// implementation. Everything else (code generation, verification, recovery codes, the
        /// challenge that spends one) ships here.
        /// </para>
        /// <para>
        /// Calling this alone changes nothing about sign-in. The challenge only runs once the app's
        /// <c>AuthenticationService</c> passes the resolved <c>ITwoFactorAuthenticator</c> to its base
        /// constructor, which is the second, explicit half of adopting the feature.
        /// </para>
        /// </remarks>
        public IServiceCollection AddTwoFactorAuthentication(IConfiguration configuration)
        {
            services.AddOptions<TwoFactorSettings>()
                .Bind(configuration.GetSection(TwoFactorSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Singleton: the service holds only bound settings and is pure over its arguments.
            services.TryAddSingleton<ITwoFactorService, TotpTwoFactorService>();

            // Scoped, because the store it challenges through shares the request's unit of work.
            services.TryAddScoped<ITwoFactorAuthenticator, TwoFactorAuthenticator>();

            return services;
        }

        /// <summary>
        /// Registers the opt-in email-confirmation token service and its
        /// <c>Authentication:EmailConfirmation</c> settings.
        /// </summary>
        /// <param name="configuration">Application configuration for binding the settings section.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// Registering it does not gate sign-in. Tokens are issued and redeemed as soon as the app
        /// wires the handler bases, and an unconfirmed account is only refused once
        /// <c>Authentication:EmailConfirmation:RequireConfirmedEmail</c> is set AND the app's
        /// <c>User</c> implements <c>IEmailConfirmableUser</c>.
        /// </remarks>
        public IServiceCollection AddEmailConfirmation(IConfiguration configuration)
        {
            services.AddOptions<EmailConfirmationSettings>()
                .Bind(configuration.GetSection(EmailConfirmationSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Scoped, matching the password-reset token service it shares a cache and a shape with.
            services.TryAddScoped<IEmailConfirmationTokenService, EmailConfirmationTokenService>();

            return services;
        }

        /// <summary>
        /// Registers the opt-in stored permission grants: the EF grant store, the cached snapshot and
        /// its refresh service, the role-administration service, and the
        /// <see cref="LayeredPermissionRegistry"/> decorator over whatever
        /// <see cref="IPermissionRegistry"/> the host already registered.
        /// </summary>
        /// <param name="configuration">Application configuration for binding the settings section.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// <b>Call it AFTER <c>AddAuthorizationPolicies()</c> (or <c>AddPermissions(...)</c>)</b>. It
        /// decorates the registered registry, so a host that calls it first has nothing to decorate;
        /// in that case an empty compiled registry is registered here so the stored layer still works
        /// on its own rather than failing at resolve time.
        /// </para>
        /// <para>
        /// This call is also what maps the table. Registering
        /// <c>PermissionGrantModelGate</c> tells <c>ApplicationDbContext</c> to apply
        /// <c>ApplyPermissionGrantConfiguration</c> to the model of the one context whose physical
        /// source is named by <c>Authentication:PermissionGrants:DataSourceName</c>, so no consumer
        /// calls that extension by hand and a host that never opts in keeps a byte-identical model
        /// (the refresh-session precedent; one database owns the rows).
        /// </para>
        /// </remarks>
        public IServiceCollection AddStoredPermissionGrants(IConfiguration configuration)
        {
            services.AddOptions<PermissionGrantSettings>()
                .Bind(configuration.GetSection(PermissionGrantSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // The model gate: its presence IS the opt-in the context reads (see the type's remarks).
            services.TryAddSingleton<Persistence.Auth.PermissionGrantModelGate>();

            services.TryAddScoped<IPermissionGrantStore, Persistence.Auth.EFPermissionGrantStore>();

            // One instance is both the read model and the invalidator, so an edit and the reads that
            // follow it cannot end up looking at two different snapshots.
            services.TryAddSingleton<Persistence.Auth.PermissionGrantCache>();
            services.TryAddSingleton<IPermissionGrantCache>(sp =>
                sp.GetRequiredService<Persistence.Auth.PermissionGrantCache>());
            services.TryAddSingleton<IPermissionGrantCacheInvalidator>(sp =>
                sp.GetRequiredService<Persistence.Auth.PermissionGrantCache>());

            services.TryAddScoped<IRoleAdministrationService, StoredPermissionRoleAdministrationService>();

            // TryAddEnumerable, not AddHostedService: two modules calling this must not start two
            // refresh loops in one process.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, Persistence.Auth.PermissionGrantRefreshService>());

            // TryDecorate returns false when nothing registered IPermissionRegistry yet. Registering an
            // empty compiled layer and decorating that keeps the call order-tolerant: the host's own
            // registry, added later with TryAdd, would simply lose to this one, so the fallback is
            // reported rather than silently swallowing the host's grants.
            if (!services.TryDecorate<IPermissionRegistry, LayeredPermissionRegistry>())
            {
                var compiled = new PermissionRegistry(
                    new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase));

                services.AddSingleton<IPermissionRegistry>(
                    sp => new LayeredPermissionRegistry(compiled, sp.GetRequiredService<IPermissionGrantCache>()));

                // The same empty instance answers as the catalog, so the administration surface has
                // something to enumerate even on a host that declared no compiled grants at all.
                services.TryAddSingleton<IPermissionCatalog>(compiled);
            }

            return services;
        }
    }
}
