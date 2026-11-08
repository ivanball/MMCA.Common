using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.MachineClients;
using MMCA.Common.Application.Auth.Passkeys;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Infrastructure.Notifications.Push;
using MMCA.Common.Infrastructure.Storage;

namespace MMCA.Common.Infrastructure.Configuration;

/// <summary>
/// Fails host startup when the configuration switches on a capability whose implementation lives in a
/// vendor adapter package that was never registered, so the inert core default is still in place.
/// <para>
/// The core packages are cloud-neutral: native push resolves to <see cref="NullNativePushSender"/>
/// until an adapter swaps it. That default is deliberately quiet (a push is dropped), which is right
/// for a host that never switched push on and wrong for one that did: a deployment that sets
/// <c>NativePush:Enabled</c> but lost the adapter call would start, pass its health checks and silently
/// stop delivering pushes. The check resolves the sender the way a request would and names the call
/// that is missing.
/// </para>
/// <para>
/// File storage deliberately has no check. A tripwire must fire on a key that declares intent, and a
/// <c>FileStorage</c> section does not: it is routinely shared configuration (one user-secrets id
/// across every service of a solution), so a host that never stores files sees it too. A host that
/// does store files and lost its adapter call is not silent either: <see cref="NullFileStorageService"/>
/// fails every upload with <c>FileStorage.NotConfigured</c>.
/// </para>
/// </summary>
/// <param name="serviceProvider">The root provider.</param>
internal sealed class CloudAdapterTripwire(IServiceProvider serviceProvider) : IValidateOptions<CloudAdapterTripwireOptions>
{
    /// <summary>The configuration key that switches native push on.</summary>
    internal const string NativePushEnabledKey = "NativePush:Enabled";

    /// <summary>
    /// The configuration key that switches passkeys on. The WebAuthn ceremony lives in the opt-in
    /// <c>MMCA.Common.Passkeys</c> package (ADR-139), so an enabled section without its registration call
    /// is the same silent failure as a push section without its adapter: every passkey route 404s.
    /// </summary>
    internal const string PasskeysEnabledKey = "Passkeys:Enabled";

    /// <summary>
    /// The configuration key that switches machine clients on (ADR-140). An enabled section without
    /// <c>AddMachineClients</c> is the same silent failure: the token endpoint and every administration
    /// route answer 404. (A missing provisioner is caught by the settings validator that call registers.)
    /// </summary>
    internal const string MachineClientsEnabledKey = "MachineClients:Enabled";

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, CloudAdapterTripwireOptions options)
    {
        var configuration = serviceProvider.GetService<IConfiguration>();
        if (configuration is null)
        {
            return ValidateOptionsResult.Skip;
        }

        List<string> failures = [];

        using (var scope = serviceProvider.CreateScope())
        {
            if (IsSwitchedOn(configuration, PasskeysEnabledKey)
                && scope.ServiceProvider.GetService<IPasskeyCeremony>() is null)
            {
                failures.Add(
                    "Passkeys:Enabled is true, but no passkey ceremony is registered, so every passkey route would answer 404. Reference MMCA.Common.Passkeys and call services.AddPasskeys(configuration), with Passkeys:RelyingPartyId and Passkeys:Origins.");
            }

            if (IsSwitchedOn(configuration, MachineClientsEnabledKey) && !IsRegistered<IMachineClientService>(scope.ServiceProvider))
            {
                failures.Add(
                    "MachineClients:Enabled is true, but machine clients are not registered, so the token endpoint and every machine-client route would answer 404. Call services.AddMachineClients(configuration) and register the app's IServiceAccountProvisioner.");
            }

            if (IsSwitchedOn(configuration, NativePushEnabledKey)
                && scope.ServiceProvider.GetService<INativePushSender>() is null or NullNativePushSender)
            {
                failures.Add(
                    "NativePush:Enabled is true, but INativePushSender is still the no-op NullNativePushSender, so no OS-level push is delivered. Either no push adapter is registered or its section is incomplete: reference MMCA.Common.Azure and call services.AddAzureNotificationHubsPush(configuration), with NativePush:ConnectionString and NativePush:HubName; or reference MMCA.Common.Aws and call services.AddAmazonSnsPush(configuration), with NativePush:Sns:InstallationsTable and NativePush:Sns:FcmPlatformApplicationArn and/or NativePush:Sns:ApnsPlatformApplicationArn; or reference MMCA.Common.Gcp and call services.AddFirebaseCloudMessagingPush(configuration), with NativePush:Fcm:ProjectId and NativePush:Fcm:InstallationsCollection.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Whether the boolean switch at <paramref name="key"/> is set to true.</summary>
    /// <param name="configuration">The host configuration.</param>
    /// <param name="key">The switch's configuration key.</param>
    /// <returns><see langword="true"/> when the key parses as true.</returns>
    private static bool IsSwitchedOn(IConfiguration configuration, string key) =>
        bool.TryParse(configuration[key], out var enabled) && enabled;

    /// <summary>Whether <typeparamref name="TService"/> is registered, asked without constructing it.</summary>
    /// <typeparam name="TService">The service type.</typeparam>
    /// <param name="services">The provider to ask.</param>
    /// <returns><see langword="true"/> when the provider can resolve it.</returns>
    private static bool IsRegistered<TService>(IServiceProvider services) =>
        services.GetService<IServiceProviderIsService>()?.IsService(typeof(TService)) != false;
}
