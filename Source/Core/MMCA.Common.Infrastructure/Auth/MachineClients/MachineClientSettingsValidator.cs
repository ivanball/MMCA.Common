using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.MachineClients;

namespace MMCA.Common.Infrastructure.Auth.MachineClients;

/// <summary>
/// Validates the <c>MachineClients</c> section at startup (ADR-140): the token lifetime within its
/// range, and, while the feature is enabled, a consumer <see cref="IServiceAccountProvisioner"/>
/// registered, so a host that switched machine clients on without the one piece of consumer code fails
/// at start naming it instead of answering every create with a resolution error.
/// </summary>
/// <param name="serviceProvider">The root provider, asked whether the provisioner is registered (never resolved).</param>
internal sealed class MachineClientSettingsValidator(IServiceProvider serviceProvider) : IValidateOptions<MachineClientSettings>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, MachineClientSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];
        if (options.AccessTokenMinutes is < MachineClientSettings.MinAccessTokenMinutes or > MachineClientSettings.MaxAccessTokenMinutes)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"MachineClients:AccessTokenMinutes must be from {MachineClientSettings.MinAccessTokenMinutes} to {MachineClientSettings.MaxAccessTokenMinutes}; a longer token would outlive the revocation marker."));
        }

        if (string.IsNullOrWhiteSpace(options.DataSourceName))
        {
            failures.Add("MachineClients:DataSourceName is required.");
        }

        if (options.Enabled
            && serviceProvider.GetService<IServiceProviderIsService>() is { } isService
            && !isService.IsService(typeof(IServiceAccountProvisioner)))
        {
            failures.Add(
                "MachineClients:Enabled is true, but no IServiceAccountProvisioner is registered. Register the app's implementation over its User aggregate (services.AddScoped<IServiceAccountProvisioner, ...>()), which creates and reads the service-account users behind machine clients.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
