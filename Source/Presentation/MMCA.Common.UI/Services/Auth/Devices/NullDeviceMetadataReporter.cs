namespace MMCA.Common.UI.Services.Auth.Devices;

/// <summary>
/// No-op <see cref="IDeviceMetadataReporter"/>: the default on every head. A browser reports no device
/// metadata, and its sessions are described from the user-agent the server records at sign-in. It
/// answers <see langword="true"/> (nothing to report), so no caller retries it.
/// </summary>
public sealed class NullDeviceMetadataReporter : IDeviceMetadataReporter
{
    /// <inheritdoc />
    public Task<bool> ReportAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
}
