using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
            if (bool.TryParse(configuration[NativePushEnabledKey], out var nativePushEnabled)
                && nativePushEnabled
                && scope.ServiceProvider.GetService<INativePushSender>() is null or NullNativePushSender)
            {
                failures.Add(
                    "NativePush:Enabled is true, but INativePushSender is still the no-op NullNativePushSender, so no OS-level push is delivered. Either no push adapter is registered or its section is incomplete: reference MMCA.Common.Azure and call services.AddAzureNotificationHubsPush(configuration), with NativePush:ConnectionString and NativePush:HubName.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
