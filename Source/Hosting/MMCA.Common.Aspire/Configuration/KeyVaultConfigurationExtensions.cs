using System.Globalization;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MMCA.Common.Aspire;

/// <summary>
/// Registration extension that layers an Azure Key Vault over the host configuration.
/// <para>
/// Connection strings, signing keys and provider client secrets have to reach the process somehow,
/// and the two easy answers are both wrong. A value checked into an appsettings file is readable by
/// everyone who can read the repository, and it stays readable in the history after it is removed.
/// A value injected as a deployment-time environment variable is readable by anything that can read
/// the process environment, and it is frozen until the next deployment, so rotating it means
/// redeploying. Reading the values from a vault under the host's own managed identity keeps them out
/// of both places and lets a rotation take effect on its own.
/// </para>
/// </summary>
public static class KeyVaultConfigurationExtensions
{
    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        /// <summary>
        /// Adds an Azure Key Vault configuration source to the host configuration, so every secret in
        /// the vault is readable through <see cref="IConfiguration"/> exactly like any other setting
        /// and overrides the sources added before it.
        /// <para>
        /// Configuration keys:
        /// <list type="bullet">
        ///   <item><c>KeyVault:Uri</c> (the gate): the vault URI, e.g.
        ///     <c>https://myvault.vault.azure.net/</c>. When absent or whitespace this method does
        ///     nothing at all, so local development and tests keep the configuration sources they
        ///     already have and take no Azure dependency at startup.</item>
        ///   <item><c>KeyVault:ReloadIntervalMinutes</c> (optional): how often the provider re-reads
        ///     the vault, in whole minutes. Left unset, the vault is read once during startup and a
        ///     rotated secret only reaches the host on its next restart.</item>
        /// </list>
        /// </para>
        /// <para>
        /// A secret name cannot contain a colon, so the provider maps a double dash onto the
        /// configuration separator: the secret <c>ConnectionStrings--Default</c> arrives as the
        /// configuration key <c>ConnectionStrings:Default</c>, and <c>Jwt--SigningKey</c> as
        /// <c>Jwt:SigningKey</c>. Name the secrets that way and the host binds them with the settings
        /// classes it already has, with no code change at the binding side.
        /// </para>
        /// <para>
        /// Authentication uses <see cref="DefaultAzureCredential"/>, so a deployed host authenticates
        /// with its managed identity and a developer machine falls back to the local Azure CLI or
        /// Visual Studio sign-in. The identity needs the Key Vault Secrets User role on the vault,
        /// which the one-time bootstrap in <c>samples/deployment/DEPLOYMENT.md</c> (step 3) already
        /// grants to the application identity, so an app deployed that way needs no extra grant.
        /// </para>
        /// <para>
        /// This is deliberately NOT called from <c>AddServiceDefaults()</c>, for the same reason
        /// <c>AddCommonDataProtection</c> is not: service defaults run in every host, a developer
        /// machine and a test host and the Helpdesk seed included, and an unconditional Azure
        /// dependency at startup would be a liability there rather than a feature. A host that wants
        /// vault-backed configuration opts in with this one call.
        /// </para>
        /// <para>
        /// The source goes onto <c>builder.Configuration</c>, whose <c>ConfigurationManager</c> builds
        /// and loads each source as it is added, so the vault is read synchronously at this point in
        /// startup. That is what makes the secrets available to everything registered after this call,
        /// and it is also why this call belongs early in the host builder, before the settings binding
        /// and the service registrations that read them.
        /// </para>
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        /// <exception cref="UriFormatException">
        /// <c>KeyVault:Uri</c> is present but is not a valid absolute URI.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// <c>KeyVault:ReloadIntervalMinutes</c> is present but is not a positive whole number.
        /// </exception>
        public TBuilder AddCommonKeyVaultConfiguration()
        {
            var vaultUri = builder.Configuration["KeyVault:Uri"];

            // The gate. Absent means "do nothing": a developer machine, a test host and the Helpdesk
            // seed all read their configuration from files and user secrets, where reaching for a vault
            // at startup buys nothing and costs a hard Azure dependency on every run.
            if (string.IsNullOrWhiteSpace(vaultUri))
            {
                return builder;
            }

            var options = new AzureKeyVaultConfigurationOptions();

            // A misspelled reload interval fails LOUDLY rather than falling back to "never reload".
            // Silently ignoring it would leave the host serving the secret values it read at startup
            // forever, and the operator would only find out when a rotated credential failed to take
            // effect, which is exactly the wrong moment.
            var reloadInterval = builder.Configuration["KeyVault:ReloadIntervalMinutes"];
            if (!string.IsNullOrWhiteSpace(reloadInterval))
            {
                if (!int.TryParse(reloadInterval, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                    || minutes <= 0)
                {
                    throw new InvalidOperationException(
                        $"KeyVault:ReloadIntervalMinutes must be a positive whole number of minutes, but was \"{reloadInterval}\".");
                }

                options.ReloadInterval = TimeSpan.FromMinutes(minutes);
            }

            builder.Configuration.AddAzureKeyVault(new Uri(vaultUri), new DefaultAzureCredential(), options);

            return builder;
        }
    }
}
