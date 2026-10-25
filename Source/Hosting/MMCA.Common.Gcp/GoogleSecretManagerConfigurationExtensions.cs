using System.Globalization;
using Google.Cloud.SecretManager.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Gcp.Secrets;

namespace MMCA.Common.Gcp;

/// <summary>
/// Registration extension that layers Google Cloud Secret Manager over the host configuration.
/// <para>
/// Connection strings, signing keys and provider client secrets have to reach the process somehow,
/// and the two easy answers are both wrong. A value checked into an appsettings file is readable by
/// everyone who can read the repository, and it stays readable in the history after it is removed.
/// A value injected as a deployment-time environment variable is readable by anything that can read
/// the process environment, and it is frozen until the next deployment, so rotating it means
/// redeploying. Reading the values from Secret Manager under the host's own service account keeps
/// them out of both places and lets a rotation take effect on its own.
/// </para>
/// </summary>
public static class GoogleSecretManagerConfigurationExtensions
{
    private const string SectionPath = "GoogleSecretManager";

    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        /// <summary>
        /// Adds a Google Cloud Secret Manager configuration source to the host configuration, so every
        /// secret under one id prefix is readable through <see cref="IConfiguration"/> exactly like
        /// any other setting and overrides the sources added before it.
        /// <para>
        /// Configuration keys:
        /// <list type="bullet">
        ///   <item><c>GoogleSecretManager:ProjectId</c> and <c>GoogleSecretManager:NamePrefix</c>
        ///     (together the gate): the project whose secrets this host reads, and the secret id
        ///     prefix, e.g. <c>adc-prod-</c>. When either is absent or whitespace this method does
        ///     nothing at all, so local development and tests keep the configuration sources they
        ///     already have and take no Google Cloud dependency at startup.</item>
        ///   <item><c>GoogleSecretManager:ReloadIntervalMinutes</c> (optional): how often the provider
        ///     re-reads the secrets, in whole minutes. Left unset, the secrets are read once during
        ///     startup and a rotated secret only reaches the host on its next restart.</item>
        /// </list>
        /// </para>
        /// <para>
        /// The naming convention is the Key Vault one, so a secret set moves between the clouds by
        /// renaming its prefix alone: the prefix is stripped and a double dash maps onto the
        /// configuration separator, so the secret <c>adc-prod-ConnectionStrings--Default</c> arrives
        /// as the configuration key <c>ConnectionStrings:Default</c>, and <c>adc-prod-Jwt--SigningKey</c>
        /// as <c>Jwt:SigningKey</c> (secret ids allow letters, digits, <c>-</c> and <c>_</c>, so the
        /// double dash is the separator here too). The <c>latest</c> version of each secret is read;
        /// a secret whose latest version is disabled or destroyed, or that has no version yet, is
        /// skipped.
        /// </para>
        /// <para>
        /// The client authenticates with Application Default Credentials (workload identity on GKE
        /// and Cloud Run, the attached service account on Compute Engine,
        /// <c>gcloud auth application-default login</c> on a workstation). The identity needs
        /// <c>secretmanager.secrets.list</c> on the project and <c>secretmanager.versions.get</c>
        /// (both in <c>roles/secretmanager.viewer</c>), plus <c>secretmanager.versions.access</c>
        /// (<c>roles/secretmanager.secretAccessor</c>) on the secrets under the prefix. A secret the
        /// identity cannot read fails startup, naming it.
        /// </para>
        /// <para>
        /// This is deliberately NOT called from <c>AddServiceDefaults()</c>, for the same reason the
        /// Key Vault sibling is not: service defaults run in every host, a developer machine and a test
        /// host included, and an unconditional Google Cloud dependency at startup would be a liability
        /// there. The source goes onto <c>builder.Configuration</c>, whose <c>ConfigurationManager</c>
        /// loads each source as it is added, so the secrets are read synchronously at this point in
        /// startup; call it early in the host builder, before the settings binding and the service
        /// registrations that read them.
        /// </para>
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// <c>GoogleSecretManager:ReloadIntervalMinutes</c> is present but is not a positive whole
        /// number; the secrets of the project cannot be listed; or a secret under the prefix cannot be
        /// read.
        /// </exception>
        public TBuilder AddGoogleSecretManagerConfiguration()
        {
            var configuration = builder.Configuration;
            var projectId = configuration[$"{SectionPath}:ProjectId"];
            var namePrefix = configuration[$"{SectionPath}:NamePrefix"];

            // The gate. Incomplete means "do nothing": a developer machine and a test host read their
            // configuration from files and user secrets, where reaching for Google Cloud at startup
            // buys nothing and costs a hard dependency on every run. No receipt either, so a prefix
            // that only appears in a source added after this call still trips the startup check.
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(namePrefix))
            {
                return builder;
            }

            // A misspelled reload interval fails LOUDLY rather than falling back to "never reload",
            // as in the Key Vault sibling: the operator would otherwise only find out when a rotated
            // credential failed to take effect.
            TimeSpan? reloadInterval = null;
            var reloadSetting = configuration[$"{SectionPath}:ReloadIntervalMinutes"];
            if (!string.IsNullOrWhiteSpace(reloadSetting))
            {
                if (!int.TryParse(reloadSetting, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                    || minutes <= 0)
                {
                    throw new InvalidOperationException(
                        $"{SectionPath}:ReloadIntervalMinutes must be a positive whole number of minutes, but was \"{reloadSetting}\".");
                }

                reloadInterval = TimeSpan.FromMinutes(minutes);
            }

            // The receipt the core startup tripwire checks: GoogleSecretManager:NamePrefix set without
            // this call fails the host instead of starting it with every secret missing.
            builder.Services.AddSingleton(new HostIntegrationMarker(HostIntegrationNames.GoogleSecretManagerConfiguration));

            configuration.Add(new GoogleSecretManagerConfigurationSource(
                SecretManagerServiceClient.Create,
                projectId,
                namePrefix,
                reloadInterval));

            return builder;
        }
    }
}
