using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Infrastructure.Notifications.Push;
using MMCA.Common.Infrastructure.Storage;

namespace MMCA.Common.Infrastructure.Configuration;

/// <summary>
/// Fails host startup when the configuration switches on a capability whose implementation lives in a
/// vendor adapter package that was never registered, so the inert core default is still in place.
/// <para>
/// The core packages are cloud-neutral: file storage and native push resolve to
/// <see cref="NullFileStorageService"/> and <see cref="NullNativePushSender"/> until an adapter swaps
/// them. Both defaults are deliberately quiet (an upload fails with a tidy error, a push is dropped),
/// which is right for a host that never configured the capability and wrong for one that did: a
/// deployment that sets <c>FileStorage</c> or enables <c>NativePush</c> but lost the adapter call
/// would start, pass its health checks and silently stop storing files or delivering pushes. Each
/// check resolves the service the way a request would and names the call that is missing.
/// </para>
/// </summary>
/// <param name="serviceProvider">The root provider.</param>
internal sealed class CloudAdapterTripwire(IServiceProvider serviceProvider) : IValidateOptions<CloudAdapterTripwireOptions>
{
    /// <summary>The file-storage configuration section an adapter binds.</summary>
    internal const string FileStorageSection = "FileStorage";

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
            if (IsFileStorageConfigured(configuration)
                && scope.ServiceProvider.GetService<IFileStorageService>() is null or NullFileStorageService)
            {
                failures.Add(
                    "The FileStorage section is configured, but IFileStorageService is still the unconfigured NullFileStorageService, so every upload fails. Either no storage adapter is registered or its section is incomplete: reference MMCA.Common.Azure and call services.AddAzureBlobFileStorage(configuration), with FileStorage:ContainerName plus FileStorage:ServiceUri or FileStorage:ConnectionString.");
            }

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

    /// <summary>
    /// A section counts as configured when any key under it carries a non-blank value. A section
    /// that ships only blank placeholders (an appsettings.json with <c>"ContainerName": ""</c>, filled
    /// in per deployment) is not configured, so a development host is left alone.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <returns><see langword="true"/> when the <c>FileStorage</c> section holds a value.</returns>
    private static bool IsFileStorageConfigured(IConfiguration configuration) =>
        configuration.GetSection(FileStorageSection)
            .AsEnumerable(makePathsRelative: true)
            .Any(pair => !string.IsNullOrWhiteSpace(pair.Value));
}
