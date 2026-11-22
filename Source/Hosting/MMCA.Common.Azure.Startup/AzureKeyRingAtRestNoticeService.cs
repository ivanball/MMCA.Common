using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Azure;

/// <summary>
/// Emits one startup Warning when the shared DataProtection key ring is persisted to Blob Storage
/// with no <c>DataProtection:KeyVaultKeyUri</c>, in a host that is not Development: the key ring XML
/// then sits in the blob without an at-rest key. Registered by <c>AddAzureDataProtection</c> only in
/// that case, so a host that configured the key logs nothing.
/// </summary>
/// <param name="logger">Logger for the startup notice.</param>
internal sealed partial class AzureKeyRingAtRestNoticeService(
    ILogger<AzureKeyRingAtRestNoticeService> logger) : IHostedService
{
    /// <summary>The configuration key the notice names.</summary>
    internal const string KeyVaultKeyUriKey = "DataProtection:KeyVaultKeyUri";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogNoAtRestKey(logger, KeyVaultKeyUriKey);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The DataProtection key ring is persisted to Azure Blob Storage without an at-rest key. Set {ConfigurationKey} to a Key Vault key URI so the key ring is encrypted in the blob.")]
    private static partial void LogNoAtRestKey(ILogger logger, string configurationKey);
}
