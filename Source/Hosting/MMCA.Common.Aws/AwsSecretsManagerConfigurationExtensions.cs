using System.Globalization;
using Amazon.SecretsManager;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Aws.Secrets;

namespace MMCA.Common.Aws;

/// <summary>
/// Registration extension that layers AWS Secrets Manager over the host configuration.
/// <para>
/// Connection strings, signing keys and provider client secrets have to reach the process somehow,
/// and the two easy answers are both wrong. A value checked into an appsettings file is readable by
/// everyone who can read the repository, and it stays readable in the history after it is removed.
/// A value injected as a deployment-time environment variable is readable by anything that can read
/// the process environment, and it is frozen until the next deployment, so rotating it means
/// redeploying. Reading the values from Secrets Manager under the host's own IAM role keeps them out
/// of both places and lets a rotation take effect on its own.
/// </para>
/// </summary>
public static class AwsSecretsManagerConfigurationExtensions
{
    private const string SectionPath = "SecretsManager";

    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        /// <summary>
        /// Adds an AWS Secrets Manager configuration source to the host configuration, so every secret
        /// under one name prefix is readable through <see cref="IConfiguration"/> exactly like any
        /// other setting and overrides the sources added before it.
        /// <para>
        /// Configuration keys:
        /// <list type="bullet">
        ///   <item><c>SecretsManager:NamePrefix</c> (the gate): the secret name prefix this host
        ///     reads, e.g. <c>adc/prod/</c>. When absent or whitespace this method does nothing at
        ///     all, so local development and tests keep the configuration sources they already have
        ///     and take no AWS dependency at startup.</item>
        ///   <item><c>SecretsManager:Region</c> (optional): the region system name, else the AWS SDK
        ///     region fallback chain.</item>
        ///   <item><c>SecretsManager:ServiceUrl</c> (optional): an endpoint override, e.g. LocalStack,
        ///     with <c>Region</c> as its signing region.</item>
        ///   <item><c>SecretsManager:AccessKeyId</c> + <c>SecretAccessKey</c> (optional, both or
        ///     neither): static credentials, else the AWS default credential chain.</item>
        ///   <item><c>SecretsManager:ReloadIntervalMinutes</c> (optional): how often the provider
        ///     re-reads the secrets, in whole minutes. Left unset, the secrets are read once during
        ///     startup and a rotated secret only reaches the host on its next restart.</item>
        /// </list>
        /// </para>
        /// <para>
        /// The naming convention is the Key Vault one, so a secret set moves between the two clouds
        /// by renaming its prefix alone: the prefix is stripped and a double dash maps onto the
        /// configuration separator, so the secret <c>adc/prod/ConnectionStrings--Default</c> arrives
        /// as the configuration key <c>ConnectionStrings:Default</c>, and <c>adc/prod/Jwt--SigningKey</c>
        /// as <c>Jwt:SigningKey</c>. Only secrets with a string value are read.
        /// </para>
        /// <para>
        /// The host identity needs <c>secretsmanager:ListSecrets</c> (which does not scope to a
        /// resource), plus <c>secretsmanager:BatchGetSecretValue</c> and
        /// <c>secretsmanager:GetSecretValue</c> on the secrets under the prefix, and
        /// <c>kms:Decrypt</c> when they are encrypted with a customer-managed key. A secret the
        /// identity cannot read fails startup, naming it.
        /// </para>
        /// <para>
        /// This is deliberately NOT called from <c>AddServiceDefaults()</c>, for the same reason the
        /// Key Vault sibling is not: service defaults run in every host, a developer machine and a test
        /// host included, and an unconditional AWS dependency at startup would be a liability there.
        /// The source goes onto <c>builder.Configuration</c>, whose <c>ConfigurationManager</c> loads
        /// each source as it is added, so the secrets are read synchronously at this point in
        /// startup; call it early in the host builder, before the settings binding and the service
        /// registrations that read them.
        /// </para>
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// Only one of <c>SecretsManager:AccessKeyId</c> / <c>SecretAccessKey</c> is set;
        /// <c>SecretsManager:ReloadIntervalMinutes</c> is present but is not a positive whole number;
        /// or a secret under the prefix cannot be read.
        /// </exception>
        public TBuilder AddAwsSecretsManagerConfiguration()
        {
            var configuration = builder.Configuration;
            var namePrefix = configuration[$"{SectionPath}:NamePrefix"];

            // The gate. Absent means "do nothing": a developer machine and a test host read their
            // configuration from files and user secrets, where reaching for AWS at startup buys
            // nothing and costs a hard dependency on every run. No receipt either, so a prefix that
            // only appears in a source added after this call still trips the startup check.
            if (string.IsNullOrWhiteSpace(namePrefix))
            {
                return builder;
            }

            // A half-configured key pair fails here, before any source is added.
            var credentials = AwsStaticCredentials.Resolve(
                configuration[$"{SectionPath}:AccessKeyId"], configuration[$"{SectionPath}:SecretAccessKey"], SectionPath);

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

            var clientConfig = new AmazonSecretsManagerConfig();
            AwsClientEndpoint.Apply(
                clientConfig,
                Uri.TryCreate(configuration[$"{SectionPath}:ServiceUrl"], UriKind.Absolute, out var serviceUrl) ? serviceUrl : null,
                configuration[$"{SectionPath}:Region"]);

            // The receipt the core startup tripwire checks: SecretsManager:NamePrefix set without this
            // call fails the host instead of starting it with every secret missing.
            builder.Services.AddSingleton(new HostIntegrationMarker(HostIntegrationNames.AwsSecretsManagerConfiguration));

            configuration.Add(new AwsSecretsManagerConfigurationSource(
                () => credentials is null ? new AmazonSecretsManagerClient(clientConfig) : new AmazonSecretsManagerClient(credentials, clientConfig),
                namePrefix,
                reloadInterval));

            return builder;
        }
    }
}
