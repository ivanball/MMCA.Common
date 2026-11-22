using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Gcp.DataProtection;

/// <summary>
/// Emits one startup Warning when the shared DataProtection key ring is persisted to Cloud Storage
/// with no <c>DataProtection:GcpKmsKeyName</c>, in a host that is not Development: the key ring XML
/// then sits in the object without an at-rest key. Registered by
/// <c>AddGoogleCloudDataProtection</c> only in that case, so a host that configured the key logs
/// nothing.
/// </summary>
/// <param name="logger">Logger for the startup notice.</param>
internal sealed partial class GcpKeyRingAtRestNoticeService(
    ILogger<GcpKeyRingAtRestNoticeService> logger) : IHostedService
{
    /// <summary>The configuration key the notice names.</summary>
    internal const string KmsKeyNameKey = "DataProtection:GcpKmsKeyName";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogNoAtRestKey(logger, KmsKeyNameKey);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The DataProtection key ring is persisted to Google Cloud Storage without an at-rest key. Set {ConfigurationKey} to a Cloud KMS key name so the key ring is encrypted in the object.")]
    private static partial void LogNoAtRestKey(ILogger logger, string configurationKey);
}
