using Microsoft.Extensions.Hosting;
using MMCA.Common.Application.Webhooks;

namespace MMCA.Common.Infrastructure.Webhooks;

/// <summary>
/// The one decision behind <see cref="WebhookSettings.AllowInsecureTargetsInDevelopment"/>: honoured
/// only when a registered <see cref="IHostEnvironment"/> reports Development. A host with no registered
/// environment is production (ADR-122).
/// </summary>
internal static class WebhookInsecureTargets
{
    /// <summary>Whether http, local and private targets are allowed in this host.</summary>
    /// <param name="settings">The webhook options.</param>
    /// <param name="environment">The host environment, or <see langword="null"/> when none is registered.</param>
    /// <returns><see langword="true"/> when the flag is set and the host is Development.</returns>
    internal static bool AreAllowed(WebhookSettings settings, IHostEnvironment? environment) =>
        settings.AllowInsecureTargetsInDevelopment && environment is not null && environment.IsDevelopment();
}
