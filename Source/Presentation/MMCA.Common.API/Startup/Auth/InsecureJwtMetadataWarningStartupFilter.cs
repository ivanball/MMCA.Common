using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.API.Startup.Auth;

/// <summary>
/// Emits one startup warning when <c>AddForwardedJwtBearer</c> resolved
/// <c>RequireHttpsMetadata</c> to <see langword="false"/> outside Development. Registered as an
/// <see cref="IStartupFilter"/> rather than logged during registration because the logging
/// providers are not configured yet while the service collection is being built, so a warning
/// written there would be dropped.
/// </summary>
/// <param name="logger">The logger the warning is written to.</param>
internal sealed partial class InsecureJwtMetadataWarningStartupFilter(
    ILogger<InsecureJwtMetadataWarningStartupFilter> logger) : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        LogInsecureJwtMetadata(logger, WebApplicationBuilderExtensions.RequireHttpsMetadataConfigKey);

        return next;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "JWT bearer metadata discovery will run over plain HTTP outside Development: RequireHttpsMetadata resolved to false via {ConfigKey} or an explicit argument. This is only safe when the authority is an internal-ingress cleartext (h2c) service URL; record that justification beside the setting in the deployment template.")]
    private static partial void LogInsecureJwtMetadata(ILogger logger, string configKey);
}
